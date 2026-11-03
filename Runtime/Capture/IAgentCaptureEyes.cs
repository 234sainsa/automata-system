using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// What the capture desk asks the world to do around one captured frame.
    /// 撮る係が、撮ったフレームの中で世界に頼む口（世界ごと・表の葉が結ぶ）。
    /// <para>
    /// ⭐ 芯の撮る係（<see cref="AgentCaptureDesk"/>）は、フレームの終わり（描き終わった後）に画面を撮り、
    ///    同じフレームの中でこの口に返事の欄を書かせる＝絵と表の look が同じフレームになる。
    /// ⭐ 絵の置き場・名前・残す枚数・1 秒 1 枚の決まりは芯が持つ。この口は「何を書くか」だけを持つ。
    /// ⚠ 主スレッドで呼ばれる。
    /// </para>
    /// </summary>
    public interface IAgentCaptureEyes
    {
        /// <summary>
        /// Checks the request before capturing; returns an error code or null.
        /// 撮る前に頼みの形を見る。悪ければ断りの字を返し（窓口は 400 で返す）、hint に直し方を入れる。良ければ null。
        /// </summary>
        string CheckRequest(JObject request, out string hint);

        /// <summary>
        /// Called before the captured frame is drawn (for example, hides debug overlays).
        /// 撮るフレームの描く前（そのフレームの PostLateUpdate）に呼ぶ。
        /// hideDebugOverlays が true の時だけ、開発用の帯などをこの 1 枚だけ隠し、隠した物の名前を <paramref name="hidden"/> へ足す。
        /// ⚠ 隠すのは、呼んだフレームと次の 1 フレームだけにする（撮れなかった時に、戻し忘れが起きない形）。
        /// </summary>
        void BeforeFrame(bool hideDebugOverlays, List<string> hidden);

        /// <summary>
        /// Called at the end of the frame, right after the capture: writes the fields of the reply (look, measure, and so on).
        /// フレームの終わり（描き終わって撮った直後・同じフレーム）に呼ぶ。返事の欄（look・measure など）を書く。
        /// </summary>
        void Describe(JsonWriter w, JObject request, IAgentCapturePixels pixels);
    }

    /// <summary>
    /// Measures the brightness of a captured frame (the same frame as the look).
    /// 撮った絵（同じフレーム）の明るさを測る口。撮る係が <see cref="IAgentCaptureEyes.Describe"/> に渡す。
    /// </summary>
    public interface IAgentCapturePixels
    {
        /// <summary>
        /// The width in pixels.
        /// 絵の幅（px）。
        /// </summary>
        int Width { get; }

        /// <summary>
        /// The height in pixels.
        /// 絵の高さ（px）。
        /// </summary>
        int Height { get; }

        /// <summary>
        /// Returns the mean brightness (0 to 1) of a screen-ratio rectangle (origin at the lower left), or null when the rectangle is outside the picture.
        /// 画面の割合の四角（左下が原点・表の look の四角と同じ）の明るさの平均（0〜1）。四角が絵の外なら null。
        /// ⭐ 明るさは、絵の色（sRGB の字のまま）を Rec.709 の重みで混ぜた値。合否は出さない（暗いかは使う側が決める）。
        /// </summary>
        float? MeanLight(Rect area);
    }
}
