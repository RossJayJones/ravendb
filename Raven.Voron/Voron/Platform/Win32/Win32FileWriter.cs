﻿// -----------------------------------------------------------------------
//  <copyright file="Win32FileWriter.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Voron.Impl.Paging;
using Voron.Trees;

namespace Voron.Platform.Win32
{
    public unsafe class Win32FileWriter : IFileWriter
    {
        private readonly IVirtualPager _pager;
        private readonly SafeFileHandle _handle;
        private bool _hasWrites;
        private Queue<ManualResetEvent> _eventsQueue = new Queue<ManualResetEvent>();
        private List<PendingWrite> _pendingWrites = new List<PendingWrite>();
        private List<Page> _pendingPages = new List<Page>();
        private const int _maxPendingWrites = 100;

        private class PendingWrite
        {
            public NativeOverlapped* NativeOverlapped;
            public ManualResetEvent Event;
            public int Status;
        }
        public Win32FileWriter(string file, IVirtualPager pager)
        {
            _pager = pager;
            _handle = Win32NativeFileMethods.CreateFile(file, Win32NativeFileAccess.GenericWrite,
                Win32NativeFileShare.Read | Win32NativeFileShare.Write | Win32NativeFileShare.Delete, IntPtr.Zero,
                Win32NativeFileCreationDisposition.OpenAlways, Win32NativeFileAttributes.Overlapped | Win32NativeFileAttributes.RandomAccess, IntPtr.Zero);

            if (_handle.IsInvalid)
                throw new Win32Exception();

            if(ThreadPool.BindHandle(_handle) == false)
                throw new Win32Exception();
        }

        public void Dispose()
        {
            _handle.Dispose();
            foreach (var e in _eventsQueue)
            {
                e.Dispose();
            }

            foreach (var pendingWrite in _pendingWrites)
            {
                pendingWrite.Event.Dispose();
            }
            if(_hasWrites)
                ((Win32MemoryMapPager)_pager).RefreshMappedView(null);
        }

        public unsafe void Write(Page page)
        {
            _hasWrites = true;
            // nothing yet, so skip it 
            if (_pendingPages.Count == 0)
            {
                _pendingPages.Add(page);
                return;
            }

            // check if we have a continious write
            var lastPage = _pendingPages[_pendingPages.Count-1];
            int pagesToWrite = lastPage.IsOverflow ? _pager.GetNumberOfOverflowPages(lastPage.OverflowSize) : 1;
            if (lastPage.PageNumber + pagesToWrite == page.PageNumber)
            {
                _pendingPages.Add(page);
                return;
            }

            FlushPendingPages();
            _pendingPages.Clear();

            _pendingPages.Add(page);
        }


        private void WaitIfHaveTooManyPendingWrites()
        {
            if (_pendingWrites.Count < _maxPendingWrites)
                return;

            var index = WaitHandle.WaitAny(GetPendingWaitHandles());
            while (_pendingWrites.Count > 0)
            {
                if (_pendingWrites[index].Status != 0)
                    throw new Win32Exception(_pendingWrites[index].Status);
                
                _eventsQueue.Enqueue(_pendingWrites[index].Event);

                _pendingWrites.RemoveAt(index);

                //now let us clear anything else that is also completed
                index = WaitHandle.WaitAny(GetPendingWaitHandles(), 0);
                if (index == WaitHandle.WaitTimeout)
                    break;
            } 
        }

        private WaitHandle[] GetPendingWaitHandles()
        {
            var events = new WaitHandle[_pendingWrites.Count];
            for (int i = 0; i < _pendingWrites.Count; i++)
            {
                events[i] = _pendingWrites[i].Event;
            }
            return events;
        }


        private void WaitForAllWritesToComplete()
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                for (int i = 0; i < _pendingWrites.Count; i++)
                {
                    _pendingWrites[i].Event.WaitOne();
                }
            }
            else
            {
                if (WaitHandle.WaitAll(GetPendingWaitHandles()) == false)
                    throw new Win32Exception();
            }
            for (int i = 0; i < _pendingWrites.Count; i++)
            {
                if(_pendingWrites[i].Status != 0)
                    throw new Win32Exception(_pendingWrites[i].Status);
            }
        }

        private unsafe void FlushPendingPages()
        {
            if (_pendingPages.Count == 0)
                return;

            WaitIfHaveTooManyPendingWrites();

            var firstPage = _pendingPages[0];

            int pagesToWrite = 0;

            for (int i = 0; i < _pendingPages.Count; i++)
            {
                pagesToWrite+=_pendingPages[i].IsOverflow ? _pager.GetNumberOfOverflowPages(_pendingPages[i].OverflowSize) : 1;
            }

            var offset = firstPage.PageNumber * AbstractPager.PageSize;

            var mre = _eventsQueue.Count > 0 ? _eventsQueue.Dequeue() : new ManualResetEvent(false);
            mre.Reset();

            var pendingWrite = new PendingWrite { Event = mre };
           
            var lo = (int) (offset & 0xffffffff);
            var hi = (int) (offset >> 32);
            var overlapped = new Overlapped(lo, hi, mre.SafeWaitHandle.DangerousGetHandle(), null);
            pendingWrite.NativeOverlapped = overlapped.Pack((code, bytes, overlap) =>
            {
                var unpack = Overlapped.Unpack(overlap);
                pendingWrite.Status = (int)code;
                Overlapped.Free(overlap);
                pendingWrite.NativeOverlapped = null;
            }, null);
            int remaining = pagesToWrite * AbstractPager.PageSize;

            int written;
            if (Win32NativeFileMethods.WriteFile(_handle, firstPage.Base, remaining, out written, pendingWrite.NativeOverlapped) == false)
            {
                var lastWin32Error = Marshal.GetLastWin32Error();
                if (lastWin32Error != Win32NativeFileMethods.ErrorIOPending)
                {
                    _eventsQueue.Enqueue(pendingWrite.Event);
                    throw new Win32Exception(lastWin32Error);
                }
                _pendingWrites.Add(pendingWrite);
            }
            else
            {
                _eventsQueue.Enqueue(pendingWrite.Event);
            }
        }

        public void Sync()
        {
            if (_hasWrites == false)
                return;

            WaitForAllWritesToComplete();

            if(Win32NativeFileMethods.FlushFileBuffers(_handle) == false)
                throw new Win32Exception();
        }


        public void EnsureContinuous(long pageNumber, int numberOfPagesInLastPage)
        {
            _pager.EnsureContinuous(null, pageNumber, numberOfPagesInLastPage);
        }
    }
}