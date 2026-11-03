namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The two faces of the API: the surface (what a player sees) and the back (developer reads).
    /// 窓口の顔＝使う側がどちらのトークンで入ったか。
    /// <para>
    /// 表（<see cref="Surface"/>）は、プレイヤーがその瞬間の画面と音で知れる物だけを読む。AI は遊ぶ判断を表だけで決める。
    /// 裏（<see cref="Back"/>）は、世界のデータを全部読む（開発と調べ物に使う。製品版には入れない）。
    /// ⚠ 既定の値（0）は表にしてある。係が書き忘れても、裏の力は付かない。
    /// </para>
    /// </summary>
    public enum AgentFace
    {
        Surface = 0,
        Back = 1,
    }

    /// <summary>
    /// Helpers for AgentFace.
    /// AgentFace に使う補助の口。
    /// </summary>
    public static class AgentFaceExtensions
    {
        /// <summary>
        /// Returns the word used on the wire for the face.
        /// 窓口の JSON に書く字（"surface"・"back"）を返す。
        /// </summary>
        public static string ToWire(this AgentFace face) => face == AgentFace.Back ? "back" : "surface";
    }
}
