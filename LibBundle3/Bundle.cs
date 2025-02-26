﻿using System;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using LibBundle3.Records;

using SystemExtensions;
using SystemExtensions.Streams;

namespace LibBundle3;

/// <summary>
/// Class to handle the *.bundle.bin file.
/// </summary>
public class Bundle : IDisposable {
	/// <summary>
	/// Metadata of a bundle file which is stored at the beginning of the file in 60 bytes.
	/// </summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential, Size = 60, Pack = 4)]
	protected struct Header {
		public int uncompressed_size;
		public int compressed_size;
		public int head_size = 48; // chunk_count * 4 + 48
		public Oodle.Compressor compressor = Oodle.Compressor.Leviathan; // Leviathan == 13
		public int unknown = 1; // 1
		public long uncompressed_size_long; // == uncompressed_size
		public long compressed_size_long; // == compressed_size
		public int chunk_count;
		public int chunk_size = 256 * 1024; // 256KB == 262144
		public int unknown3 = 0; // 0
		public int unknown4 = 0; // 0
		public int unknown5 = 0; // 0
		public int unknown6 = 0; // 0

		/// <summary>
		/// Initialize a <see cref="Header"/> instance with default values of a empty bundle (Not same as <see langword="default"/>).
		/// </summary>
		public Header() { }

		/// <returns>Size of decompressed Chunks[Chunks.Length - 1] in bytes</returns>
		public readonly int GetLastChunkSize() {
			return uncompressed_size - (chunk_size * (chunk_count - 1));
		}
	}

	/// <summary>
	/// Record of the <see cref="Bundle"/> instance, not <see langword="null"/> when this instance is created by <see cref="Index"/>.
	/// </summary>
	public virtual BundleRecord? Record { get; }

	/// <summary>
	/// Size of the uncompressed content in bytes, synced with <see cref="BundleRecord.UncompressedSize"/> of <see cref="Record"/>.
	/// </summary>
	public virtual int UncompressedSize {
		get => metadata.uncompressed_size;
		internal set => metadata.uncompressed_size = value; // See Index.CreateBundle
	}
	/// <summary>
	/// Size of the compressed content in bytes.
	/// </summary>
	public virtual int CompressedSize => metadata.compressed_size;

	protected readonly Stream baseStream;
	/// <summary>
	/// If false, close the <see cref="baseStream"/> when <see cref="Dispose"/>.
	/// </summary>
	protected readonly bool leaveOpen;
	protected Header metadata;
	/// <summary>
	/// Sizes of each compressed chunk in bytes.
	/// </summary>
	protected int[] compressed_chunk_sizes;
	/// <summary>
	/// Cached data of the full decompressed content, use <see cref="cacheTable"/> to determine the initialization of each chunk.
	/// </summary>
	protected byte[]? cachedContent;
	/// <summary>
	/// Indicate whether the corresponding chunk of <see cref="cachedContent"/> is initialized.
	/// </summary>
	protected bool[]? cacheTable;

	/// <param name="filePath">Path of the bundle file on disk</param>
	/// <param name="record">Record of this bundle file</param>
	/// <exception cref="FileNotFoundException" />
	public Bundle(string filePath, BundleRecord? record = null) :
		this(File.Open(Utils.ExpandPath(filePath), FileMode.Open, FileAccess.ReadWrite, FileShare.Read), false, record) { }

	/// <param name="stream">Stream of the bundle file</param>
	/// <param name="leaveOpen">If false, close the <paramref name="stream"/> when this instance is disposed</param>
	/// <param name="record">Record of this bundle file</param>
	public unsafe Bundle(Stream stream, bool leaveOpen = false, BundleRecord? record = null) {
		ArgumentNullException.ThrowIfNull(stream);
		if (!BitConverter.IsLittleEndian)
			ThrowHelper.Throw<NotSupportedException>("Big-endian architecture is not supported");
		baseStream = stream;
		this.leaveOpen = leaveOpen;
		Record = record;
		lock (stream) {
			stream.Position = 0;
			stream.Read(out metadata);
			if (record is not null)
				record.UncompressedSize = metadata.uncompressed_size;
			stream.Read(compressed_chunk_sizes = GC.AllocateUninitializedArray<int>(metadata.chunk_count));
		}
	}

	/// <summary>
	/// Internal used by <see cref="Index.CreateBundle"/>.
	/// </summary>
	/// <param name="stream">Stream of the bundle to write (which will be cleared)</param>
	/// <param name="record">Record of the bundle</param>
	protected internal unsafe Bundle(Stream stream, BundleRecord? record) {
		ArgumentNullException.ThrowIfNull(stream);
		baseStream = stream;
		Record = record;
		compressed_chunk_sizes = [];
		lock (stream) {
			stream.Position = 0;
			metadata = new();
			stream.Write(in metadata);
			stream.SetLength(stream.Position);
		}
	}

	/// <summary>
	/// Read the whole data of the bundle without caching.
	/// </summary>
	public byte[] ReadWithoutCache() {
		lock (baseStream) {
			var result = GC.AllocateUninitializedArray<byte>(metadata.uncompressed_size);
			ReadChunks(result, 0, metadata.chunk_count);
			return result;
		}
	}
	/// <summary>
	/// Read the whole data of the bundle without caching.
	/// </summary>
	/// <param name="span">Span to save the data read</param>
	/// <returns>Size of the data read in bytes.</returns>
	/// <exception cref="ArgumentOutOfRangeException"/>
	public int ReadWithoutCache(Span<byte> span) {
		lock (baseStream) {
			ArgumentOutOfRangeException.ThrowIfLessThan(span.Length, metadata.uncompressed_size);
			ReadChunks(span, 0, metadata.chunk_count);
			return metadata.uncompressed_size;
		}
	}
	/// <summary>
	/// Read the data with the given <paramref name="offset"/> and <paramref name="length"/> without caching.
	/// </summary>
	/// <returns>
	/// The data read. You can do anything with it cause we don't keep any reference to it.
	/// </returns>
	/// <exception cref="ArgumentOutOfRangeException"/>
	public ArraySegment<byte> ReadWithoutCache(int offset, int length) {
		lock (baseStream) {
			unchecked {
				ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)offset, (uint)metadata.uncompressed_size);
				if (length == 0)
					return ArraySegment<byte>.Empty;
				ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)length, (uint)(metadata.uncompressed_size - offset));
			}
			var start = Math.DivRem(offset, metadata.chunk_size, out var remainder);
			var end = (offset + length - 1) / metadata.chunk_size + 1;
			var result = GC.AllocateUninitializedArray<byte>(end == metadata.chunk_count // Contains the last chunk
				? metadata.uncompressed_size - metadata.chunk_size * start : metadata.chunk_size * (end - start));
			ReadChunks(result, start, end);
			return new(result, remainder, length);
		}
	}

	/// <summary>
	/// Read the whole data of the bundle (use cached data if exists).
	/// </summary>
	/// <remarks>Use <see cref="ReadWithoutCache()"/> instead if you'll read only once</remarks>
	public ReadOnlyMemory<byte> Read() {
		lock (baseStream) {
			cachedContent ??= GC.AllocateUninitializedArray<byte>(metadata.uncompressed_size);
			ReadChunks(cachedContent, 0, metadata.chunk_count);
			return cachedContent;
		}
	}
	/// <summary>
	/// Read the data with the given <paramref name="offset"/> and <paramref name="length"/> (use cached data if exists).
	/// </summary>
	/// <remarks>Use <see cref="ReadWithoutCache(int, int)"/> instead if you'll read only once or the range is far apart each call</remarks>
	/// <exception cref="ArgumentOutOfRangeException"/>
	public ReadOnlyMemory<byte> Read(int offset, int length) {
		lock (baseStream) {
			unchecked {
				ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)offset, (uint)metadata.uncompressed_size);
				if (length == 0)
					return ReadOnlyMemory<byte>.Empty;
				ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)length, (uint)(metadata.uncompressed_size - offset));
			}
			var start = offset / metadata.chunk_size;
			var end = (offset + length - 1) / metadata.chunk_size + 1;
			cachedContent ??= GC.AllocateUninitializedArray<byte>(metadata.uncompressed_size);
			ReadChunks(cachedContent.AsSpan(start * metadata.chunk_size), start, end);
			return new(cachedContent, offset, length);
		}
	}

	/// <summary>
	/// Read data from compressed chunk(with size <see cref="Header.chunk_size"/>)
	/// start from index = <paramref name="start"/> and combine them to a <see cref="byte"/>[] without caching.
	/// </summary>
	/// <param name="start">Index of the beginning chunk</param>
	/// <param name="end">Index of the ending chunk (exclusive)</param>
	/// <remarks>
	/// Internal implementation of other Read methods.
	/// <para>There's no arguments checking as the upper methods did it.</para>
	/// </remarks>
	/// <exception cref="ArgumentOutOfRangeException"/>
	protected virtual unsafe void ReadChunks(Span<byte> span, int start, int end, bool cached = false) {
		Debug.Assert(span.Length >= (end == metadata.chunk_count // Contains the last chunk
			? metadata.uncompressed_size - metadata.chunk_size * start : metadata.chunk_size * (end - start)));
		Debug.Assert((uint)start <= (uint)metadata.chunk_count);
		Debug.Assert((uint)end <= (uint)metadata.chunk_count);

		if (start == end) // Shouldn't happen
			return;

		lock (baseStream) {
			EnsureNotDisposed();
			Oodle.Initialize(new() { ChunkSize = metadata.chunk_size, Compressor = metadata.compressor, EnableCompressing = false });
			baseStream.Position = (sizeof(int) * 3) + metadata.head_size + compressed_chunk_sizes.Take(start).Sum();
			if (cached)
				cacheTable ??= new bool[metadata.chunk_count];

			var last = metadata.chunk_count - 1;
			var compressed = ArrayPool<byte>.Shared.Rent(Oodle.GetCompressedBufferSize());
			try {
				fixed (byte* ptr = span, tmp = compressed) {
					var p = ptr;
					for (var i = start; i < end; ++i) {
						if (cached && cacheTable![i]) {
							baseStream.Seek(compressed_chunk_sizes[i], SeekOrigin.Current);
						} else {
							baseStream.ReadExactly(new(tmp, compressed_chunk_sizes[i]));
							if (i == last) {
								last = metadata.GetLastChunkSize();
								if (Oodle.Decompress(tmp, compressed_chunk_sizes[i], p, last) != last)
									ThrowHelper.Throw<Exception>("Failed to decompress last chunk with index: " + i);
							} else {
								if (Oodle.Decompress(tmp, compressed_chunk_sizes[i], p) != metadata.chunk_size)
									ThrowHelper.Throw<Exception>("Failed to decompress chunk with index: " + i);
							}
						}
						p += metadata.chunk_size;
					}
				}
			} finally {
				ArrayPool<byte>.Shared.Return(compressed);
			}
		}
	}

	/// <summary>
	/// Remove all the cached data of this instance.
	/// </summary>
	public virtual void RemoveCache() {
		cachedContent = null;
		cacheTable = null;
	}

	/// <summary>
	/// Save the bundle with new contents.
	/// </summary>
	public virtual unsafe void Save(scoped ReadOnlySpan<byte> newContent, Oodle.CompressionLevel compressionLevel = Oodle.CompressionLevel.Normal) {
		lock (baseStream) {
			EnsureNotDisposed();
			RemoveCache();

			Oodle.Initialize(new() { ChunkSize = metadata.chunk_size, Compressor = metadata.compressor, CompressionLevel = compressionLevel, EnableCompressing = true });
			metadata.uncompressed_size_long = metadata.uncompressed_size = newContent.Length;
			metadata.chunk_count = metadata.uncompressed_size / metadata.chunk_size;
			if (metadata.uncompressed_size > metadata.chunk_count * metadata.chunk_size)
				++metadata.chunk_count;
			metadata.head_size = metadata.chunk_count * sizeof(int) + (sizeof(Header) - sizeof(int) * 3);
			baseStream.Position = sizeof(int) * 3 + metadata.head_size;
			compressed_chunk_sizes = GC.AllocateUninitializedArray<int>(metadata.chunk_count);
			metadata.compressed_size = 0;
			var compressed = ArrayPool<byte>.Shared.Rent(Oodle.GetCompressedBufferSize());
			try {
				fixed (byte* ptr = newContent, tmp = compressed) {
					var p = ptr;
					var last = metadata.chunk_count - 1;
					int l;
					for (var i = 0; i < last; ++i) {
						l = (int)Oodle.Compress(p, tmp);
						compressed_chunk_sizes[i] = l;
						metadata.compressed_size += l;
						p += metadata.chunk_size;
						baseStream.Write(new(tmp, l));
					}
					l = (int)Oodle.Compress(p, metadata.GetLastChunkSize(), tmp);
					compressed_chunk_sizes[last] = l;
					metadata.compressed_size += l;
					baseStream.Write(new(tmp, l));
				}
			} finally {
				ArrayPool<byte>.Shared.Return(compressed);
			}
			metadata.compressed_size_long = metadata.compressed_size;

			baseStream.Position = 0;
			baseStream.Write(in metadata);
			baseStream.Write(compressed_chunk_sizes);

			baseStream.SetLength((sizeof(int) * 3) + metadata.head_size + metadata.compressed_size);
			baseStream.Flush();
			if (Record is not null)
				Record.UncompressedSize = metadata.uncompressed_size;
		}
	}

	protected internal virtual void EnsureNotDisposed() {
		ObjectDisposedException.ThrowIf(!baseStream.CanRead, this);
	}

	/// <summary>
	/// Get the field of the base stream of this instance.
	/// Using this method may cause dangerous unexpected behavior.
	/// </summary>
	[EditorBrowsable(EditorBrowsableState.Advanced)]
	public ref Stream UnsafeGetStream() {
		return ref Unsafe.AsRef(in baseStream);
	}

	public virtual void Dispose() {
		GC.SuppressFinalize(this);
		lock (baseStream) {
			RemoveCache();
			if (!leaveOpen)
				baseStream.Close();
		}
	}
}