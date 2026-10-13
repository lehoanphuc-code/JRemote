namespace JRemote.Rendering.Decoders;

public static class H264FrameParser
{
    /// <summary>
    /// Searches for Annex-B start codes (0x000001 or 0x00000001) in raw H.264 streams.
    /// </summary>
    public static IEnumerable<ArraySegment<byte>> ExtractNalUnits(byte[] buffer, int offset, int length)
    {
        int end = offset + length;
        int nalStart = -1;
        int nalHeaderLen = 0;

        for (int i = offset; i < end - 3; i++)
        {
            if (buffer[i] == 0 && buffer[i + 1] == 0)
            {
                int prefixLen = 0;
                if (buffer[i + 2] == 1) prefixLen = 3;
                else if (i < end - 4 && buffer[i + 2] == 0 && buffer[i + 3] == 1) prefixLen = 4;

                if (prefixLen > 0)
                {
                    if (nalStart != -1)
                    {
                        int nalLen = i - nalStart;
                        if (nalLen > 0)
                        {
                            yield return new ArraySegment<byte>(buffer, nalStart, nalLen);
                        }
                    }
                    nalStart = i + prefixLen;
                    nalHeaderLen = prefixLen;
                    i += prefixLen - 1;
                }
            }
        }

        if (nalStart != -1 && nalStart < end)
        {
            yield return new ArraySegment<byte>(buffer, nalStart, end - nalStart);
        }
    }
}
