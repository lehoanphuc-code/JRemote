﻿using System;
namespace JRemote.iOS.PlayFair
{
    public struct H264Data
    {
        public int FrameType { get; set; }
        public byte[] Data { get; set; }
        public int Length { get; set; }
        public long Pts { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
