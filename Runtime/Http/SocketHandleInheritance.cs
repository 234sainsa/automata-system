using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Monster.Balloon.AutomataSystem.Http
{
    /// <summary>
    /// 窓口のソケットに「子プロセスへ受け継がせない」印を付ける。
    /// <para>
    /// ⚠ Unity（Mono）のソケットは、そのままでは子プロセスへ受け継がれる。Unity が子プロセス（コンパイラ・シェーダーの係など）を
    ///   起こすと、子プロセスがソケットの持ち手を持ったままになる。すると窓口を閉じても、子プロセスが終わるまで OS 上では口が開いたまま残る
    ///   （実測＝Play を抜けた後も 127.0.0.1:47810 が LISTENING のまま残り、子プロセスが終わると消えた）。
    /// ⭐ Windows では SetHandleInformation で印を付ける。ほかの OS では、今は印を付けない（窓口を使うのは Windows の PC とスマホ）。
    /// </para>
    /// </summary>
    internal static class SocketHandleInheritance
    {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private const uint HandleFlagInherit = 0x00000001;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        /// <summary>印を付ける。付けられなかった時は false と、その理由を返す。</summary>
        public static bool TryDisable(Socket socket, out string why)
        {
            if (SetHandleInformation(socket.Handle, HandleFlagInherit, 0))
            {
                why = null;
                return true;
            }

            why = "SetHandleInformation failed (win32 error " + Marshal.GetLastWin32Error() + ")";
            return false;
        }
#else
        /// <summary>この OS では印を付けない（false と理由を返す）。</summary>
        public static bool TryDisable(Socket socket, out string why)
        {
            why = "not supported on this platform";
            return false;
        }
#endif
    }
}
