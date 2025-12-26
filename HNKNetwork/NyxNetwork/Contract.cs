using System.Runtime.InteropServices;

namespace NyxNetwork
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ContractHeader
    {
        public const byte Header = 0x0F;
        public ushort Length;
        public ushort Method;
    }

    public enum Constant : ushort
    {
        HEALTHCHECK = 0x00,
        ACK = 0x01,
    }
}
