// Graceful Ctrl-C: first press aborts the in-flight VDI set, second kills.

using System;
using System.Collections.Generic;
using SqlSqueeze.Core;
using VdiNet.VirtualBackupDevice;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class CancelTests
    {
        private class FakeDeviceSet : IVirtualDeviceSet
        {
            public int AbortSignalled;

            public void CreateEx(string instanceName, string deviceSetName, VirtualDeviceSetConfig config) { throw new NotImplementedException(); }
            public VirtualDeviceSetConfig GetConfiguration(TimeSpan? timeout) { throw new NotImplementedException(); }
            public IVirtualDevice OpenDevice(string deviceName) { throw new NotImplementedException(); }
            public void SignalAbort() { AbortSignalled++; }
            public void Close() { throw new NotImplementedException(); }
            public void Dispose() { }
        }

        [Fact]
        public void RequestCancel_WithNothingInFlight_ReturnsFalse()
        {
            OperationCanceller.Reset();

            Assert.False(BackupPipeSystem.RequestCancel());
            Assert.False(OperationCanceller.OnCancelKeyPress());
        }

        [Fact]
        public void FirstPress_SignalsAbort_SecondPressLetsDie()
        {
            OperationCanceller.Reset();
            FakeDeviceSet set = new FakeDeviceSet();
            BackupPipeSystem.TrackDeviceSet(set);
            try
            {
                Assert.True(OperationCanceller.OnCancelKeyPress());
                Assert.Equal(1, set.AbortSignalled);

                Assert.False(OperationCanceller.OnCancelKeyPress());
                Assert.Equal(1, set.AbortSignalled);
            }
            finally
            {
                BackupPipeSystem.UntrackDeviceSet(set);
            }
        }

        [Fact]
        public void Untrack_RestoresNothingInFlight()
        {
            OperationCanceller.Reset();
            FakeDeviceSet set = new FakeDeviceSet();
            BackupPipeSystem.TrackDeviceSet(set);
            BackupPipeSystem.UntrackDeviceSet(set);

            Assert.False(OperationCanceller.OnCancelKeyPress());
            Assert.Equal(0, set.AbortSignalled);
        }

        [Fact]
        public void Untrack_OnlyRemovesItsOwnSet()
        {
            OperationCanceller.Reset();
            FakeDeviceSet first = new FakeDeviceSet();
            FakeDeviceSet second = new FakeDeviceSet();
            BackupPipeSystem.TrackDeviceSet(first);
            BackupPipeSystem.TrackDeviceSet(second);
            try
            {
                BackupPipeSystem.UntrackDeviceSet(first);

                Assert.True(OperationCanceller.OnCancelKeyPress());
                Assert.Equal(0, first.AbortSignalled);
                Assert.Equal(1, second.AbortSignalled);
            }
            finally
            {
                BackupPipeSystem.UntrackDeviceSet(second);
            }
        }
    }
}
