﻿using Pipelines.Sockets.Unofficial.Internal;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Pipelines.Sockets.Unofficial.Arenas
{
    /// <summary>
    /// A Stream backed by a read-only ReadSequence of bytes
    /// </summary>
    public abstract class ReadOnlySequenceStream : Stream
    {
        /// <summary>
        /// Create a ReadOnlySequenceStream based on an existing sequence
        /// </summary>
        public static ReadOnlySequenceStream Create(in ReadOnlySequence<byte> sequence)
            => sequence.IsEmpty ? Empty : new ReadOnlySequenceStreamImpl(sequence);

        /// <summary>
        /// An empty stream
        /// </summary>
        public static ReadOnlySequenceStream Empty { get; } = new ReadOnlySequenceStreamImpl(default);

        /// <summary>
        /// Gets the underlying buffer associated with this stream
        /// </summary>
        public ReadOnlySequence<byte> GetBuffer() => GetReadOnlyBuffer();

        private protected abstract ReadOnlySequence<byte> GetReadOnlyBuffer();

        /// <summary>
        /// See Stream.CanRead
        /// </summary>
        public override bool CanRead => true;

        /// <summary>
        /// See Stream.CanSeek
        /// </summary>
        public override bool CanSeek => true;

        /// <summary>
        /// See Stream.FlushAsync
        /// </summary>
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// See Stream.Flush
        /// </summary>
        public override void Flush() { }

        /// <summary>
        /// See Stream.CanTimeout
        /// </summary>
        public override bool CanTimeout => false;

        /// <summary>
        /// See Stream.Seek
        /// </summary>
        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Current:
                    Position += offset;
                    break;
                case SeekOrigin.Begin:
                    Position = offset;
                    break;
                case SeekOrigin.End:
                    Position = Length + offset;
                    break;
                default:
                    Throw.ArgumentOutOfRange(nameof(origin));
                    break;
            }
            return Position;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private protected static void CopySegment(in ReadOnlyMemory<byte> buffer, Stream destination)
        {
#if SOCKET_STREAM_BUFFERS
            destination.Write(buffer.Span);
#else
            if(MemoryMarshal.TryGetArray(buffer, out var segment))
            {
                destination.Write(segment.Array, segment.Offset, segment.Count);
            }
            else
            {
                var span = buffer.Span;
                var arr = ArrayPool<byte>.Shared.Rent(span.Length);
                span.CopyTo(arr);
                destination.Write(arr, 0, span.Length);
                ArrayPool<byte>.Shared.Return(arr);
            }
#endif
        }

        private protected static void CopyTo(in ReadOnlySequence<byte> sequence, Stream destination)
        {
            if (sequence.IsSingleSegment)
            {
                CopySegment(sequence.First, destination);
            }
            else
            {
                foreach (var segment in sequence)
                {
                    CopySegment(segment, destination);
                }
            }
        }

        protected private struct FastState // not actually a buffer as such - just prevents
        {                                   // us constantly having to slice etc
            public void Init(in SequencePosition position, long remaining)
            {
                if (position.GetObject() is ReadOnlySequenceSegment<byte> segment
                    && MemoryMarshal.TryGetArray(segment.Memory, out var array))
                {
                    int offset = position.GetInteger();
                    _array = array.Array;
                    _offset = offset; // the net offset into the array
                    _count = (int)Math.Min( // the smaller of (noting it will always be an int)
                            array.Count - offset, // the amount left in this buffer
                            remaining); // the logical amount left in the stream
                }
                else
                {
                    this = default;
                }
            }

            public override string ToString() => $"{_count} bytes remaining";

            byte[] _array;
            int _count, _offset;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int TryRead(Span<byte> span)
            {
                var bytes = Math.Min(span.Length, _count);
                if (bytes != 0)
                {
                    new Span<byte>(_array, _offset, bytes).CopyTo(span);
                    _count -= bytes;
                    _offset += bytes;
                }
                return bytes;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryWrite(ReadOnlySpan<byte> span)
            {
                int bytes = span.Length;
                if (_count < bytes) return false;
                span.CopyTo(new Span<byte>(_array, _offset, bytes));
                _count -= bytes;
                _offset += bytes;
                return true;
            }
        }
    }

    /// <summary>
    /// A Stream backed by a read-write Sequence of bytes
    /// </summary>
    public abstract class SequenceStream : ReadOnlySequenceStream
    {
#if DEBUG
        internal static long LeaseCount => Volatile.Read(ref SequenceStreamImpl.s_leaseCount);
#endif

        /// <summary>
        /// Create a new dynamically expandable SequenceStream
        /// </summary>
        public static SequenceStream Create(long minCapacity = 0, long maxCapacity = long.MaxValue, MemoryPool<byte> pool = default)
            => maxCapacity == 0 ? s_empty : new SequenceStreamImpl(minCapacity, maxCapacity, pool);

        /// <summary>
        /// Create a new SequenceStream based on an existing sequence; it will not be possible to expand the stream past the initial capacity
        /// </summary>
        public static SequenceStream Create(in Sequence<byte> sequence)
            => sequence.IsEmpty ? s_empty : new SequenceStreamImpl(sequence);

        private static readonly SequenceStreamImpl s_empty = new SequenceStreamImpl(0, 0, null);

        /// <summary>
        /// Gets the underlying buffer associated with this stream
        /// </summary>
        public new Sequence<byte> GetBuffer() => GetReadWriteBuffer();

        private protected sealed override ReadOnlySequence<byte> GetReadOnlyBuffer() => GetReadWriteBuffer();

        private protected abstract Sequence<byte> GetReadWriteBuffer();

        internal abstract long Capacity { get; }

        /// <summary>
        /// Release any unnecessary sequence segments
        /// </summary>
        public abstract void Trim();

        // for debugging only
        internal int[] GetSegmentSizes()
        {
            if (!(GetReadWriteBuffer().Start.GetObject() is SequenceSegment<byte> root))
                return Array.Empty<int>();

            int count = 0;
            var segment = root;
            while (segment != null)
            {
                count++;
                segment = segment.Next;
            }

            var arr = new int[count];
            segment = root;
            count = 0;
            while (segment != null)
            {
                arr[count++] = segment.Length;
                segment = segment.Next;
            }
            return arr;
        }
    }

    [Flags]
    internal enum StreamFlags
    {
        None,
        Disposed = 1,
        IsOwner = 2,
    }
    internal static class FlagUtils
    {
        [MethodImpl]
        internal static bool HasFlag(ref int flags, StreamFlags flag)
            => (Volatile.Read(ref flags) & (int)flag) != 0;

        [MethodImpl]
        internal static void SetFlag(ref int flags, StreamFlags flag, bool value)
        {
            int oldVal, newVal;
            do
            {
                oldVal = Volatile.Read(ref flags);
                newVal = value ? oldVal | (int)flag : oldVal &~(int)flag;
            } while (oldVal != newVal && Interlocked.CompareExchange(ref flags, newVal, oldVal) != oldVal);
        }
    }



    internal sealed partial class SequenceStreamImpl : SequenceStream
    {
        private int _flags;
        private Sequence<byte> _sequence;
        private long _length, _capacity, _position;
        private readonly long _minCapacity, _maxCapacity;

#if DEBUG
        internal static long s_leaseCount;
#endif

        internal override long Capacity => _capacity;

        private class LeasedSegment : SequenceSegment<byte>
        {
            internal static LeasedSegment Create(int minimumSize, LeasedSegment previous, MemoryPool<byte> pool)
            {
                var leased = pool.Rent(minimumSize);
#if DEBUG
                Interlocked.Increment(ref s_leaseCount);
#endif

                // try and get from the pool, noting that we might be squabbling
                for (int i = 0; i < 5; i++)
                {
                    LeasedSegment oldHead;
                    if (Volatile.Read(ref s_poolSize) == 0
                        || (oldHead = Volatile.Read(ref s_poolHead)) == null) break;
                    var newHead = (LeasedSegment)oldHead.Next;

                    if (Interlocked.CompareExchange(ref s_poolHead, newHead, oldHead) == oldHead)
                    {
                        Interlocked.Decrement(ref s_poolSize); // doesn't need to be hard in lock-step with the actual length
                        oldHead.Init(leased, previous);
                        return oldHead;
                    }
                }
                return new LeasedSegment(leased, previous);
            }

            static void PushToPool(LeasedSegment segment)
            {
                if (segment == null) return;
                for (int i = 0; i < 5; i++)
                {
                    if (Volatile.Read(ref s_poolSize) >= MAX_RECYCLED) return;

                    var oldHead = Volatile.Read(ref s_poolHead);
                    segment.SetNext(oldHead);
                    if (Interlocked.CompareExchange(ref s_poolHead, segment, oldHead) == oldHead)
                    {
                        Interlocked.Increment(ref s_poolSize); // doesn't need to be hard in lock-step with the actual length
                        return;
                    }
                }
            }

            const int MAX_RECYCLED = 50; // hold a small number of LeasedSegment tokens for re-use
            static LeasedSegment s_poolHead;
            static int s_poolSize;

            private IMemoryOwner<byte> _lease;
            private LeasedSegment(IMemoryOwner<byte> lease, LeasedSegment previous) : base(lease.Memory, previous)
            {
                _lease = lease;
            }
            internal void Init(IMemoryOwner<byte> lease, LeasedSegment previous)
            {
                Init(lease.Memory, previous);
                _lease = lease;
            }

            internal void CascadeRelease(bool inclusive)
            {
                var segment = inclusive ? this : (LeasedSegment)ResetNext();
                while (segment != null)
                {
                    segment.ReleaseMemory();
#if DEBUG
                    Interlocked.Decrement(ref s_leaseCount);
#endif
                    var next = (LeasedSegment)segment.ResetNext();
                    PushToPool(segment);
                    segment = next;
                }
            }

            private void ReleaseMemory()
            {
                ResetMemory();
                var lease = _lease;
                _lease = null;
                lease?.Dispose();
            }
        }

        private LeasedSegment GetLeaseHead() => _sequence.Start.GetObject() as LeasedSegment;

        private LeasedSegment GetLeaseTail() => _sequence.End.GetObject() as LeasedSegment;

        private bool IsDisposed => FlagUtils.HasFlag(ref _flags, StreamFlags.Disposed);
        private bool IsOwner => FlagUtils.HasFlag(ref _flags, StreamFlags.IsOwner);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                FlagUtils.SetFlag(ref _flags, StreamFlags.Disposed, true);
                var leased = GetLeaseHead();
                _sequence = default;
                _length = _capacity = _position = 0;

                leased?.CascadeRelease(inclusive: true);
            }
        }

        partial void AssertValid();

#if DEBUG
        partial void AssertValid()
        {
            if (!IsDisposed) // all bets are off once disposed
            {
                Debug.Assert(_minCapacity >= 0 & _maxCapacity >= 0, "invalid min/max capacity");
                Debug.Assert(_capacity == _sequence.Length, "capacity should match sequence length");
                Debug.Assert(_capacity >= _minCapacity, "undersized capacity");
                Debug.Assert(_capacity <= _maxCapacity, "oversized capacity");
                Debug.Assert(_length >= 0 & Length <= _capacity, "invalid length");
                Debug.Assert(_position >= 0 & _position <= _length, "invalid position");
                if (IsOwner) Debug.Assert(_pool != null, "expected a pool");
                else Debug.Assert(_pool == null, "didn't expect a pool");
            }
        }
#endif

        private FastState _fastState;

        public override bool CanWrite => true;
        private readonly MemoryPool<byte> _pool;

        internal SequenceStreamImpl(long minCapacity, long maxCapacity, MemoryPool<byte> pool)
        {
            _pool = pool ?? MemoryPool<byte>.Shared;
            _flags |= (int)StreamFlags.IsOwner;
            if (minCapacity < 0) Throw.ArgumentOutOfRange(nameof(minCapacity));
            if (maxCapacity < minCapacity) Throw.ArgumentOutOfRange(nameof(maxCapacity));
            _minCapacity = minCapacity;
            _maxCapacity = maxCapacity;
            if (minCapacity != 0) ExpandCapacity(minCapacity);
            AssertValid();
        }

        public SequenceStreamImpl(in Sequence<byte> sequence)
        {
            _length = _minCapacity = _maxCapacity = _capacity = sequence.Length; // don't allow trim/expand to mess with the sequence
            _sequence = sequence;
            AssertValid();
        }

        private protected override Sequence<byte> GetReadWriteBuffer()
            => _sequence.Slice(0, _length); // don't over-report, since we don't have to

        public override void Write(byte[] buffer, int offset, int count) => WriteSpan(new ReadOnlySpan<byte>(buffer, offset, count));
        public override long Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _position;
            set
            {
                if (_position < 0 | _position > _length) Throw.ArgumentOutOfRange(nameof(Position));
                _position = value;
                InitFastState();
                AssertValid();
            }
        }

        public override long Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _length;
        }

        public override void SetLength(long value)
        {
            if (value == _length)
            {
                // nothing to do
            }
            else if (value < 0)
            {
                Throw.ArgumentOutOfRange(nameof(value));
            }
            else if (value < _length)
            {
                // getting smaller; simple
                _position = Math.Min(_position, value);
                _length = value;
            }
            else
            {
                // getting bigger
                var oldLen = _length;
                ExpandLength(value);
                _sequence.Slice(oldLen, value - oldLen).Clear(); // wipe
            }
            InitFastState();
            AssertValid();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExpandLength(long length)
        {
            if (length > _length)
            {
                if (_capacity < length) ExpandCapacity(length);
                _length = length;
            }
            AssertValid();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ExpandCapacity(long length)
        {
            if (IsDisposed) Throw.ObjectDisposed(GetType().Name);

            if (!IsOwner)
                Throw.InvalidOperation("The Stream is not dynamically expandable as it is based on a pre-existing sequence");

            if (length > _maxCapacity)
                Throw.InvalidOperation("The stream cannot be expanded beyond the maximum capacity specified at construction");

            var head = GetLeaseHead();
            var tail = GetLeaseTail();

            const int MinBlockSize = 1024;

            long needed = length - _capacity;
            Debug.Assert(needed > 0, "expected to grow capacity");

            int takeFromTail = -1, maxBlockSize = Math.Max(256 * 1024, _pool.MaxBufferSize);
            while (needed > 0)
            {
                int delta;
                if (needed >= maxBlockSize | _capacity >= maxBlockSize)
                {   // nice and simple
                    delta = maxBlockSize;
                }
                else
                {
                    // we don't want tiny little blocks; let's take the larger
                    // of "what we want" and "half again the current capacity"
                    // (note we know that both values are in "int" range now)
                    delta = (int)Math.Max(needed, _capacity / 2);

                    // apply a hard lower limit
                    delta = Math.Max(delta, MinBlockSize);
                }

                if (_capacity + delta > _maxCapacity)
                {
                    delta = checked((int)(_maxCapacity - _capacity));
                }

                if (delta <= 0) Throw.InvalidOperation("Error expanding chain");

                tail = LeasedSegment.Create(delta, tail, _pool);
                if (head == null) head = tail;

                // note: we might not want to take all of what we are given, because of max-capacity
                // (the lease can be larger than what we actually ask for)
                takeFromTail = _capacity + tail.Length <= _maxCapacity ? tail.Length : delta;
                needed -= takeFromTail;
                _capacity += takeFromTail;
            }

            _sequence = new Sequence<byte>(head, tail, 0, takeFromTail);
            AssertValid();
        }

        public override void Trim()
        {
            AssertValid();

            if (GetLeaseHead() == null) return; // only applies to leased chunks

            var keep = Math.Max(_length, _minCapacity);
            if (keep == _capacity) return; // can't release anything

            // we have spare capacity; release the *next* block
            var retain = (LeasedSegment)_sequence.GetPosition(keep).GetObject();
            retain.CascadeRelease(inclusive: false);
            _capacity = _sequence.Length;

            AssertValid();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadSpan(new Span<byte>(buffer, offset, count));

        public override int ReadByte()
        {
            Span<byte> span = stackalloc byte[1];
            return ReadSpan(span) <= 0 ? -1 : span[0];
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(Read(buffer, offset, count));
            }
            catch (Exception ex)
            {
                return Task.FromException<int>(ex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadSpan(Span<byte> buffer)
        {
            int bytes = _fastState.TryRead(buffer);
            if (bytes != 0)
            {
                _position += bytes;
            }
            else
            {
                bytes = SlowReadSpan(buffer);
            }
            AssertValid();
            return bytes;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void InitFastState() => _fastState.Init(_sequence.GetPosition(_position), _length - _position);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int SlowReadSpan(Span<byte> buffer)
        {
            int bytes = (int)Math.Min(buffer.Length, _length - _position);
            if (bytes <= 0) return 0;
            _sequence.Slice(_position, bytes).CopyTo(buffer);
            _position += bytes;
            InitFastState();
            return bytes;
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            try
            {
                CopyTo(_sequence.Slice(_position, _length - _position), destination);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        public override void WriteByte(byte value)
        {
            ReadOnlySpan<byte> span = stackalloc byte[1] { value };
            WriteSpan(span);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                WriteSpan(new Span<byte>(buffer, offset, count));
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteSpan(ReadOnlySpan<byte> span)
        {
            if (_fastState.TryWrite(span))
            {
                _position += span.Length;
            }
            else
            {
                SlowWriteSpan(span);
            }
            AssertValid();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SlowWriteSpan(ReadOnlySpan<byte> span)
        {
            if (!span.IsEmpty)
            {
                ExpandLength(_position + span.Length);

                var target = _sequence.Slice(_position);
                Debug.Assert(target.Length >= span.Length, $"insufficient target length; needed {span.Length}, but only {target.Length} available");
                span.CopyTo(target);
                _position += span.Length;
                InitFastState();
            }
        }

#if SOCKET_STREAM_BUFFERS
        public override void CopyTo(Stream destination, int bufferSize)
            => CopyTo(_sequence.Slice(_position, _length - _position), destination);
        public override int Read(Span<byte> buffer) => ReadSpan(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return new ValueTask<int>(ReadSpan(buffer.Span));
            }
            catch (Exception ex)
            {
                return new ValueTask<int>(Task.FromException<int>(ex));
            }
        }
        public override void Write(ReadOnlySpan<byte> buffer) => WriteSpan(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                WriteSpan(buffer.Span);
                return default;
            }
            catch (Exception ex)
            {
                return new ValueTask(Task.FromException(ex));
            }
        }
#endif
    }

    internal sealed class ReadOnlySequenceStreamImpl : ReadOnlySequenceStream
    {
        private readonly ReadOnlySequence<byte> _sequence;
        private readonly long _length;
        private long _position;
        private FastState _fastState;

        private protected override ReadOnlySequence<byte> GetReadOnlyBuffer() => _sequence;

        internal ReadOnlySequenceStreamImpl(in ReadOnlySequence<byte> sequence)
        {
            _sequence = sequence;
            _length = sequence.Length;
        }

        public override bool CanWrite => false;

        public override void Write(byte[] buffer, int offset, int count) => Throw.NotSupported();
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            Throw.NotSupported();
            return default;
        }
        public override void EndWrite(IAsyncResult asyncResult)
            => Throw.NotSupported();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Throw.NotSupported();
            return default;
        }
        public override void WriteByte(byte value) => Throw.NotSupported();
        public override long Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _position;
            set
            {
                if (_position < 0 | _position > _length) Throw.ArgumentOutOfRange(nameof(Position));
                _position = value;
                InitFastState();
            }
        }

        public override long Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _length;
        }

        public override void SetLength(long value)
        {
            if (value != _length) Throw.NotSupported();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadSpan(new Span<byte>(buffer, offset, count));

        public override int ReadByte()
        {
            Span<byte> span = stackalloc byte[1];
            return ReadSpan(span) <= 0 ? -1 : span[0];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadSpan(Span<byte> buffer)
        {
            int bytes = _fastState.TryRead(buffer);
            if (bytes != 0)
            {
                _position += bytes;
            }
            else
            {
                bytes = SlowReadSpan(buffer);
            }
            return bytes;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void InitFastState() => _fastState.Init(_sequence.GetPosition(_position), _length - _position);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int SlowReadSpan(Span<byte> buffer)
        {
            int bytes = (int)Math.Min(buffer.Length, _length - _position);
            if (bytes <= 0) return 0;
            _sequence.Slice(_position, bytes).CopyTo(buffer);
            _position += bytes;
            InitFastState();
            return bytes;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(Read(buffer, offset, count));
            }
            catch (Exception ex)
            {
                return Task.FromException<int>(ex);
            }
        }



        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            try
            {
                CopyTo(_sequence.Slice(_position), destination);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

#if SOCKET_STREAM_BUFFERS
        public override void CopyTo(Stream destination, int bufferSize) => CopyTo(_sequence.Slice(_position), destination);
        public override int Read(Span<byte> buffer) => ReadSpan(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return new ValueTask<int>(ReadSpan(buffer.Span));
            }
            catch (Exception ex)
            {
                return new ValueTask<int>(Task.FromException<int>(ex));
            }
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Throw.NotSupported();
            return default;
        }
        public override void Write(ReadOnlySpan<byte> buffer) => Throw.NotSupported();
#endif
    }
}