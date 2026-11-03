using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEditor.Media;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Monster.Balloon.AutomataSystem.Editor
{
    /// <summary>
    /// 録画の 1 本（MP4 1 つ）。<see cref="AgentBackRecorder"/> が作って閉じる。
    /// <para>
    /// ⭐ 画面を撮るのはフレームの終わり（<see cref="ShootAtEndOfFrame"/>）。小さい絵に縮めて GPU から裏で受け取り、受け取った時に MP4 へ足す。
    /// ⭐ 1 本目の絵は、同じ名前の .jpg にも書く（ファイルの一覧で中身が分かる・向きの確かめにも使う）。
    /// ⚠ 閉じる（<see cref="Close"/>）まで MP4 は再生できない。Unity が落ちると、その 1 本は壊れたまま残る。
    /// </para>
    /// </summary>
    internal sealed class AgentRecordingSegment
    {
        /// <summary>1 秒に足す絵の数。</summary>
        public const int FramesPerSecond = 15;

        /// <summary>絵の長い辺（短い辺は画面の形に合わせる）。</summary>
        public const int LongEdge = 960;

        /// <summary>同時に待つ受け取りの数。越えた分の絵は撮らない（GPU が遅れている時に積まない）。</summary>
        private const int MaxInFlight = 2;

        /// <summary>同じ絵を続けて足す上限（コマ）。これより長く空いた分は、動画では縮める。</summary>
        private const int MaxRepeat = FramesPerSecond * 2;

        /// <summary>受け取り待ちの絵ごとの、動画の中のコマの番号（頼んだ順に受け取る）。</summary>
        private readonly Queue<int> slots = new Queue<int>();

        private readonly int screenWidth;
        private readonly int screenHeight;
        private readonly string path;
        private MediaEncoder encoder;
        private RenderTexture full;
        private RenderTexture small;
        private int inFlight;
        private bool thumbnailWritten;

        public AgentRecordingSegment(string path, int screenWidth, int screenHeight)
        {
            this.path = path;
            this.screenWidth = screenWidth;
            this.screenHeight = screenHeight;
            FitSize(screenWidth, screenHeight, out int width, out int height);
            this.Width = width;
            this.Height = height;

            this.full = new RenderTexture(screenWidth, screenHeight, 0, RenderTextureFormat.ARGB32) { name = "AgentRecordingFull" };
            this.small = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = "AgentRecordingSmall" };
            this.full.Create();
            this.small.Create();

            var video = new VideoTrackAttributes
            {
                frameRate = new MediaRational(FramesPerSecond),
                width = (uint)width,
                height = (uint)height,
                includeAlpha = false,
                bitRateMode = VideoBitrateMode.Medium,
            };
            this.encoder = new MediaEncoder(path, video);
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>足した絵の数。</summary>
        public int Frames { get; private set; }

        public bool IsOpen => this.encoder != null;

        /// <summary>絵を受け取った時に起きた例外（最初の 1 つ・無ければ null）。</summary>
        public Exception Failure { get; private set; }

        /// <summary>この 1 本が、今の画面の大きさと合っているか（違えば次の 1 本に替える）。</summary>
        public bool Matches(int width, int height) => width == this.screenWidth && height == this.screenHeight;

        /// <summary>まだ絵を受け取っている途中か（多すぎる時は撮らない）。</summary>
        public bool Saturated => this.inFlight >= MaxInFlight;

        /// <summary>
        /// フレームの終わりに撮る（主スレッド・<see cref="AgentHostBehaviour.AtEndOfFrame"/> から）。
        /// <paramref name="slot"/> は、この絵を置く動画の中のコマの番号（撮っていた本当の時間 × 15）。前の絵からあいたコマは、この絵を続けて足して埋める。
        /// ⚠ 例外はここで受け止めずに投げる＝呼ぶ側が黄で 1 回鳴らして録画をやめる（赤にすると Error Pause が Play を止める）。
        /// </summary>
        public void ShootAtEndOfFrame(int slot)
        {
            if (!this.IsOpen || this.Saturated || !this.Matches(Screen.width, Screen.height))
            {
                return;
            }

            // ⭐ Blit は描く先（RenderTexture.active）を this.small に替えたままにする。録画の係は、描く先を元に戻す。
            //    戻さないと、同じフレームの終わりに後から撮る撮影の道具（ScreenCapture.CaptureScreenshotAsTexture）が
            //    画面ではなく真っ黒な絵を返す（測った結果＝描く先が AgentRecordingSmall の時 明るさ 0.000・null に戻すと 0.273）。
            RenderTexture before = RenderTexture.active;
            try
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(this.full);

                // ⭐ D3D などでは、撮った絵が上下逆さに入る＝縮める時に上下を返す。
                if (SystemInfo.graphicsUVStartsAtTop)
                {
                    Graphics.Blit(this.full, this.small, new Vector2(1f, -1f), new Vector2(0f, 1f));
                }
                else
                {
                    Graphics.Blit(this.full, this.small);
                }
            }
            finally
            {
                RenderTexture.active = before;
            }

            this.inFlight++;
            this.slots.Enqueue(slot);
            AsyncGPUReadback.Request(this.small, 0, TextureFormat.RGBA32, this.OnReadback);
        }

        /// <summary>MP4 を閉じる（ここで初めて再生できるファイルになる）。絵の入れ物も消す。</summary>
        public void Close()
        {
            MediaEncoder closing = this.encoder;
            this.encoder = null;
            closing?.Dispose();
            Release(ref this.full);
            Release(ref this.small);
        }

        /// <summary>画面の形のまま、長い辺を <see cref="LongEdge"/> にした大きさ（H.264 のため縦横とも偶数）。</summary>
        internal static void FitSize(int screenWidth, int screenHeight, out int width, out int height)
        {
            float scale = Mathf.Min(1f, LongEdge / (float)Mathf.Max(screenWidth, screenHeight));
            width = Mathf.Max(2, Mathf.RoundToInt(screenWidth * scale) & ~1);
            height = Mathf.Max(2, Mathf.RoundToInt(screenHeight * scale) & ~1);
        }

        private void OnReadback(AsyncGPUReadbackRequest request)
        {
            this.inFlight--;
            int slot = this.slots.Count > 0 ? this.slots.Dequeue() : this.Frames;
            if (!this.IsOpen || request.hasError)
            {
                return;
            }

            // ⚠ 受け取りの知らせの中で投げると赤になる＝ここで受け止め、録画の係が次の update で黄にして録画をやめる。
            try
            {
                NativeArray<byte> data = request.GetData<byte>();

                // ⭐ 動画の時間を本当の時間に合わせる＝この絵の番号まで、同じ絵を続けて足す（少なくとも 1 コマ）。
                int repeat = Mathf.Clamp(slot - this.Frames + 1, 1, MaxRepeat);
                for (int i = 0; i < repeat; i++)
                {
                    this.encoder.AddFrame(this.Width, this.Height, this.Width * 4, TextureFormat.RGBA32, data);
                    this.Frames++;
                }

                if (!this.thumbnailWritten)
                {
                    this.thumbnailWritten = true;
                    NativeArray<byte> jpg = ImageConversion.EncodeNativeArrayToJPG(data, GraphicsFormat.R8G8B8A8_UNorm, (uint)this.Width, (uint)this.Height, 0, 75);
                    try
                    {
                        File.WriteAllBytes(Path.ChangeExtension(this.path, ".jpg"), jpg.ToArray());
                    }
                    finally
                    {
                        jpg.Dispose();
                    }
                }
            }
            catch (Exception e)
            {
                this.Failure ??= e;
            }
        }

        private static void Release(ref RenderTexture texture)
        {
            if (texture != null)
            {
                texture.Release();
                Object.DestroyImmediate(texture);
                texture = null;
            }
        }
    }
}
