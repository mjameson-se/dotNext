using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.MemoryMappedFiles;
using Xunit;

namespace DotNext.IO.MemoryMappedFiles
{
    [ExcludeFromCodeCoverage]
    public sealed class ReadOnlySequenceAccessorTests : Test
    {
        [Fact]
        public static void IteratingOverSegments()
        {
            var tempFile = Path.GetTempFileName();
            var content = new byte[1024];
            new Random().NextBytes(content);
            using (var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.Write(content);
            }

            using var mappedFile = MemoryMappedFile.CreateFromFile(tempFile, FileMode.Open, null, content.Length, MemoryMappedFileAccess.Read);
            using var accessor = mappedFile.CreateSequenceAccessor(129, content.Length);
            var sequence = accessor.Sequence;
            Equal(content.Length, sequence.Length);
            False(sequence.IsSingleSegment);
            
            var offset = 0;
            for (var position = sequence.Start; sequence.TryGet(ref position, out var block) && !block.IsEmpty; offset += block.Length)
            {
                True(block.Length <= 129);
                True(new ReadOnlySpan<byte>(content, offset, block.Length).SequenceEqual(block.Span));
            }
        }
    }
}