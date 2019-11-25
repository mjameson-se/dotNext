﻿using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static System.Diagnostics.Debug;
using static System.Runtime.CompilerServices.Unsafe;
using MemoryMarshal = System.Runtime.InteropServices.MemoryMarshal;

namespace DotNext.IO
{
    using Buffers;
    using Text;
    using Intrinsics = Runtime.Intrinsics;

    /// <summary>
    /// Represents high-level read/write methods for the stream.
    /// </summary>
    /// <remarks>
    /// This class provides alternative way to read and write typed data from/to the stream
    /// without instantiation of <see cref="BinaryReader"/> and <see cref="BinaryWriter"/>.
    /// </remarks>
    public static class StreamExtensions
    {
        /// <summary>
        /// Writes the string to the stream using supplied reusable buffer.
        /// </summary>
        /// <remarks>
        /// This method doesn't encode the length of the string.
        /// </remarks>
        /// <param name="stream">The stream to write into.</param>
        /// <param name="value">The string to be encoded.</param>
        /// <param name="context">The encoding.</param>
        /// <param name="buffer">The buffer allocated by the caller needed for characters encoding.</param>
        public static void WriteString(this Stream stream, ReadOnlySpan<char> value, in EncodingContext context, Span<byte> buffer)
        {
            if (value.IsEmpty)
                return;
            var encoder = context.GetEncoder();
            var maxChars = buffer.Length / context.Encoding.GetMaxByteCount(1);
            if (maxChars == 0)
                throw new ArgumentException(ExceptionMessages.BufferTooSmall, nameof(buffer));
            for (int charsLeft = value.Length, charsUsed; charsLeft > 0; value = value.Slice(charsUsed), charsLeft -= charsUsed)
            {
                charsUsed = Math.Min(maxChars, charsLeft);
                encoder.Convert(value.Slice(0, charsUsed), buffer, charsUsed == charsLeft, out charsUsed, out var bytesUsed, out _);
                stream.Write(buffer.Slice(0, bytesUsed));
            }
        }

        /// <summary>
        /// Writes the string to the stream.
        /// </summary>
        /// <remarks>
        /// This method doesn't encode the length of the string.
        /// </remarks>
        /// <param name="stream">The stream to write into.</param>
        /// <param name="value">The string to be encoded.</param>
        /// <param name="encoding">The encoding.</param>
        public static void WriteString(this Stream stream, ReadOnlySpan<char> value, Encoding encoding)
        {
            if (value.IsEmpty)
                return;
            var bytesCount = encoding.GetByteCount(value);
            using MemoryRental<byte> buffer = bytesCount <= 1024 ? stackalloc byte[bytesCount] : new MemoryRental<byte>(bytesCount);
            encoding.GetBytes(value, buffer.Span);
            stream.Write(buffer.Span);
        }

        /// <summary>
        /// Writes the string to the stream asynchronously using supplied reusable buffer.
        /// </summary>
        /// <remarks>
        /// This method doesn't encode the length of the string.
        /// </remarks>
        /// <param name="stream">The stream to write into.</param>
        /// <param name="value">The string to be encoded.</param>
        /// <param name="context">The encoding context.</param>
        /// <param name="buffer">The buffer allocated by the caller needed for characters encoding.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The task representing asynchronous state of the operation.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        public static async ValueTask WriteStringAsync(this Stream stream, ReadOnlyMemory<char> value, EncodingContext context, Memory<byte> buffer, CancellationToken token = default)
        {
            if (value.IsEmpty)
                return;
            var encoder = context.GetEncoder();
            var maxChars = buffer.Length / context.Encoding.GetMaxByteCount(1);
            if (maxChars == 0)
                throw new ArgumentException(ExceptionMessages.BufferTooSmall, nameof(buffer));
            for (int charsLeft = value.Length, charsUsed; charsLeft > 0; value = value.Slice(charsUsed), charsLeft -= charsUsed)
            {
                charsUsed = Math.Min(maxChars, charsLeft);
                encoder.Convert(value.Span.Slice(0, charsUsed), buffer.Span, charsUsed == charsLeft, out charsUsed, out var bytesUsed, out _);
                await stream.WriteAsync(buffer.Slice(0, bytesUsed), token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Writes the string to the stream asynchronously.
        /// </summary>
        /// <remarks>
        /// This method doesn't encode the length of the string.
        /// </remarks>
        /// <param name="stream">The stream to write into.</param>
        /// <param name="value">The string to be encoded.</param>
        /// <param name="encoding">The encoding context.</param>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The task representing asynchronous state of the operation.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        public static async ValueTask WriteStringAsync(this Stream stream, ReadOnlyMemory<char> value, Encoding encoding, CancellationToken token = default)
        {
            if (value.IsEmpty)
                return;
            var bytesCount = encoding.GetByteCount(value.Span);
            using var buffer = new ArrayRental<byte>(bytesCount);
            encoding.GetBytes(value.Span, buffer.Span);
            await stream.WriteAsync(buffer.Memory, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads the string using the specified encoding and supplied reusable buffer.
        /// </summary>
        /// <remarks>
        /// <paramref name="buffer"/> length can be less than <paramref name="length"/>
        /// but should be enough to decode at least one character of the specified encoding.
        /// </remarks>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="length">The length of the string, in bytes.</param>
        /// <param name="context">The text decoding context.</param>
        /// <param name="buffer">The buffer that is allocated by the caller.</param>
        /// <returns>The string decoded from the log entry content stream.</returns>
        /// <exception cref="ArgumentException"><paramref name="buffer"/> to small for decoding characters.</exception>
        /// <exception cref="EndOfStreamException">Unexpected end of stream.</exception>
        public static string ReadString(this Stream stream, int length, in DecodingContext context, Span<byte> buffer)
        {
            if (length == 0)
                return string.Empty;
            var maxChars = context.Encoding.GetMaxCharCount(buffer.Length);
            if (maxChars == 0)
                throw new ArgumentException(ExceptionMessages.BufferTooSmall, nameof(buffer));
            var decoder = context.GetDecoder();
            using var charBuffer = maxChars <= 1024 ? stackalloc char[maxChars] : new MemoryRental<char>(maxChars);
            using var result = length <= 1024 ? stackalloc char[length] : new MemoryRental<char>(length);
            int resultOffset;
            for (resultOffset = 0; length > 0;)
            {
                var n = stream.Read(buffer.Slice(0, Math.Min(length, buffer.Length)));
                if (n == 0)
                    throw new EndOfStreamException();
                length -= n;
                var charsRead = decoder.GetChars(buffer.Slice(0, n), charBuffer.Span, length == 0);
                Intrinsics.Copy(ref charBuffer[0], ref result[resultOffset], charsRead);
                resultOffset += charsRead;
            }
            return new string(result.Span.Slice(0, resultOffset));
        }

        /// <summary>
        /// Reads the string using the specified encoding.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="length">The length of the string, in bytes.</param>
        /// <param name="encoding">The encoding used to decode bytes from stream into characters.</param>
        /// <returns>The string decoded from the log entry content stream.</returns>
        /// <exception cref="EndOfStreamException">Unexpected end of stream.</exception>
        public static string ReadString(this Stream stream, int length, Encoding encoding)
        {
            if (length == 0)
                return string.Empty;
            using MemoryRental<byte> bytesBuffer = length <= 1024 ? stackalloc byte[length] : new MemoryRental<byte>(length);
            using MemoryRental<char> charBuffer = length <= 1024 ? stackalloc char[length] : new MemoryRental<char>(length);
            if (bytesBuffer.Length != stream.Read(bytesBuffer.Span))
                throw new EndOfStreamException();
            var charCount = encoding.GetChars(bytesBuffer.Span, charBuffer.Span);
            return new string(charBuffer.Span.Slice(0, charCount));
        }

        /// <summary>
        /// Reads the string asynchronously using the specified encoding and supplied reusable buffer.
        /// </summary>
        /// <remarks>
        /// <paramref name="buffer"/> length can be less than <paramref name="length"/>
        /// but should be enough to decode at least one character of the specified encoding.
        /// </remarks>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="length">The length of the string, in bytes.</param>
        /// <param name="context">The text decoding context.</param>
        /// <param name="buffer">The buffer that is allocated by the caller.</param>
        /// <param name="token">The token that can be used to cancel asynchronous operation.</param>
        /// <returns>The string decoded from the log entry content stream.</returns>
        /// <exception cref="ArgumentException"><paramref name="buffer"/> to small for decoding characters.</exception>
        /// <exception cref="EndOfStreamException">Unexpected end of stream.</exception>
        public static async ValueTask<string> ReadStringAsync(this Stream stream, int length, DecodingContext context, Memory<byte> buffer, CancellationToken token = default)
        {
            if (length == 0)
                return string.Empty;
            var maxChars = context.Encoding.GetMaxCharCount(buffer.Length);
            if (maxChars == 0)
                throw new ArgumentException(ExceptionMessages.BufferTooSmall, nameof(buffer));
            var decoder = context.GetDecoder();
            using var continuousBuffer = new ArrayRental<char>(maxChars + length);
            var charBuffer = continuousBuffer.Memory.Slice(0, maxChars);
            var result = continuousBuffer.Memory.Slice(maxChars);
            Assert(result.Length == length);
            int resultOffset;
            for (resultOffset = 0; length > 0;)
            {
                var n = await stream.ReadAsync(buffer.Slice(0, Math.Min(length, buffer.Length)), token).ConfigureAwait(false);
                if (n == 0)
                    throw new EndOfStreamException();
                length -= n;
                var charsRead = decoder.GetChars(buffer.Span.Slice(0, n), charBuffer.Span, length == 0);
                Intrinsics.Copy(ref charBuffer.Span[0], ref result.Span[resultOffset], charsRead);
                resultOffset += charsRead;
            }
            return new string(result.Span.Slice(0, resultOffset));
        }

        /// <summary>
        /// Reads the string asynchronously using the specified encoding and supplied reusable buffer.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="length">The length of the string, in bytes.</param>
        /// <param name="encoding">The encoding used to decode bytes from stream into characters.</param>
        /// <returns>The string decoded from the log entry content stream.</returns>
        /// <exception cref="EndOfStreamException">Unexpected end of stream.</exception>
        public static async ValueTask<string> ReadStringAsync(this Stream stream, int length, Encoding encoding)
        {
            if (length == 0)
                return string.Empty;
            using var bytesBuffer = new ArrayRental<byte>(length);
            using var charBuffer = new ArrayRental<char>(length);
            if (bytesBuffer.Length != await stream.ReadAsync(bytesBuffer.Memory).ConfigureAwait(false))
                throw new EndOfStreamException();
            var charCount = encoding.GetChars(bytesBuffer.Span, charBuffer.Span);
            return new string(charBuffer.Span.Slice(0, charCount));
        }

        /// <summary>
        /// Deserializes the value type from the stream.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <typeparam name="T">The value type to be deserialized.</typeparam>
        /// <returns>The value deserialized from the stream.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream is reached.</exception>
        public static unsafe T Read<T>(this Stream stream)
            where T : unmanaged
        {
            var result = default(T);
            return stream.Read(Intrinsics.AsSpan(ref result)) == sizeof(T) ? result : throw new EndOfStreamException();
        }

        /// <summary>
        /// Asynchronously deserializes the value type from the stream.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="buffer">The buffer that is allocated by the caller.</param>
        /// <param name="token">The token that can be used to cancel asynchronous operation.</param>
        /// <typeparam name="T">The value type to be deserialized.</typeparam>
        /// <returns>The value deserialized from the stream.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream is reached.</exception>
        public static async ValueTask<T> ReadAsync<T>(this Stream stream, Memory<byte> buffer, CancellationToken token = default)
            where T : unmanaged
        {
            var bytesRead = await stream.ReadAsync(buffer.Slice(0, SizeOf<T>()), token).ConfigureAwait(false);
            return bytesRead == SizeOf<T>() ? MemoryMarshal.Read<T>(buffer.Span) : throw new EndOfStreamException();
        }

        /// <summary>
        /// Asynchronously deserializes the value type from the stream.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="token">The token that can be used to cancel asynchronous operation.</param>
        /// <typeparam name="T">The value type to be deserialized.</typeparam>
        /// <returns>The value deserialized from the stream.</returns>
        /// <exception cref="EndOfStreamException">The end of the stream is reached.</exception>
        public static async ValueTask<T> ReadAsync<T>(this Stream stream, CancellationToken token = default)
            where T : unmanaged
        {
            using var buffer = new ArrayRental<byte>(SizeOf<T>());
            return await ReadAsync<T>(stream, buffer.Memory, token);
        }

        /// <summary>
        /// Serializes value to the stream.
        /// </summary>
        /// <param name="stream">The stream to write into.</param>
        /// <param name="value">The value to be written into the stream.</param>
        /// <typeparam name="T">The value type to be serialized.</typeparam>
        public static unsafe void Write<T>(this Stream stream, in T value) where T : unmanaged => stream.Write(Intrinsics.AsReadOnlySpan(in value));

        /// <summary>
        /// Asynchronously serializes value to the stream.
        /// </summary>
        /// <param name="stream">The stream to write into.</param>
        /// <param name="value">The value to be written into the stream.</param>
        /// <param name="buffer">The buffer that is used for serialization.</param>
        /// <param name="token">The token that can be used to cancel asynchronous operation.</param>
        /// <typeparam name="T">The value type to be serialized.</typeparam>
        /// <returns>The task representing asynchronous st</returns>
        public static ValueTask WriteAsync<T>(this Stream stream, T value, Memory<byte> buffer, CancellationToken token = default)
            where T : unmanaged
        {
            MemoryMarshal.Write(buffer.Span, ref value);
            return stream.WriteAsync(buffer.Slice(0, SizeOf<T>()), token);
        }

        /// <summary>
        /// Asynchronously serializes value to the stream.
        /// </summary>
        /// <param name="stream">The stream to write into.</param>
        /// <param name="value">The value to be written into the stream.</param>
        /// <param name="token">The token that can be used to cancel asynchronous operation.</param>
        /// <typeparam name="T">The value type to be serialized.</typeparam>
        /// <returns>The task representing asynchronous st</returns>
        public static async ValueTask WriteAsync<T>(this Stream stream, T value, CancellationToken token = default)
            where T : unmanaged
        {
            using var buffer = new ArrayRental<byte>(SizeOf<T>());
            await WriteAsync(stream, value, buffer.Memory, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously reads the bytes from the source stream and writes them to another stream, using a specified buffer.
        /// </summary>
        /// <param name="source">The source stream to read from.</param>
        /// <param name="destination">The destination stream to write into.</param>
        /// <param name="buffer">The buffer used to hold copied content temporarily.</param>
        /// <param name="token">The token that can be used to cancel this operation.</param>
        /// <returns>The total number of copied bytes.</returns>
        public static async ValueTask<long> CopyToAsync(this Stream source, Stream destination, Memory<byte> buffer, CancellationToken token = default)
        {
            var totalBytes = 0L;
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                totalBytes += count;
                await destination.WriteAsync(buffer.Slice(0, count), token).ConfigureAwait(false);
            }
            return totalBytes;
        }

        /// <summary>
        /// Synchronously reads the bytes from the source stream and writes them to another stream, using a specified buffer.
        /// </summary>
        /// <param name="source">The source stream to read from.</param>
        /// <param name="destination">The destination stream to write into.</param>
        /// <param name="buffer">The buffer used to hold copied content temporarily.</param>
        /// <param name="token">The token that can be used to cancel this operation.</param>
        /// <returns>The total number of copied bytes.</returns>
        public static long CopyTo(this Stream source, Stream destination, Span<byte> buffer, CancellationToken token = default)
        {
            var totalBytes = 0L;
            int count;
            while ((count = source.Read(buffer)) > 0)
            {
                totalBytes += count;
                token.ThrowIfCancellationRequested();
                destination.Write(buffer.Slice(0, count));
            }
            return totalBytes;
        }
    }
}