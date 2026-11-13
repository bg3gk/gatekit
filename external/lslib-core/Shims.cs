// Managed stand-in for the one LSLibNative call the compiled LSLib sources make.
// LSLibNative is C++/CLI (Windows only); this keeps the build portable.
using System.IO;
using K4os.Compression.LZ4.Streams;

namespace LSLib.Native
{
    /// <summary>Decompresses an LZ4 frame (used by "solid" packages of older games).</summary>
    public static class LZ4FrameCompressor
    {
        public static byte[] Decompress(byte[] frame)
        {
            // A solid segment holds the blocks of one frame but not the end mark after them. The
            // native decoder (LZ4F_decompress) stops when the input runs out; so does this one.
            using var input = new MemoryStream(frame);
            using var decoder = LZ4Stream.Decode(input);
            using var output = new MemoryStream();
            var buffer = new byte[0x10000];
            try
            {
                int n;
                while ((n = decoder.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, n);
                }
            }
            catch (EndOfStreamException) when (input.Position == input.Length)
            {
            }
            return output.ToArray();
        }
    }
}

namespace SharpGLTF.Runtime
{
    // Compression.cs imports this namespace without using anything from it; declaring it here
    // spares a dependency on the glTF libraries.
    internal static class UnusedImport
    {
    }
}
