using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// A host of tools (for example a tool server) that the endpoint can call.
    /// AI の道具の受け手。AI の口（外の AI の道具の窓口など）は、道具の表をここから読み、AI の道具の呼び出しをここへ渡す。
    /// <para>
    /// ⭐ 口と受け手を分ける＝口を差し替えても、道具の中身はゲームの側に 1 つ。
    /// ⭐ 受け手は「AI が知った事」を覚えて、同じ知らせを 2 回渡さない。数えるのは <see cref="Delivered"/> を呼ばれた時だけ
    ///    （AI に届かなかった答えの中身を数えると、AI は二度とその知らせを知れない）。
    /// </para>
    /// </summary>
    public interface IAgentToolHost
    {
        /// <summary>
        /// The tools this host offers.
        /// AI に渡す道具（名前・説明・入力の JSON Schema）。
        /// </summary>
        IReadOnlyList<AgentToolSpec> Tools { get; }

        /// <summary>
        /// Runs one tool by name.
        /// 道具を 1 回呼ぶ。どのスレッドから呼んでもよい。
        /// ⚠ 例外は投げない（知らない道具・形の悪い引数・ゲームの断りも、答えの字で返す）。
        /// </summary>
        /// <param name="name">道具の名前。</param>
        /// <param name="arguments">AI が渡した引数（JSON のオブジェクトのはず・null は空のオブジェクト）。</param>
        /// <param name="cancel">AI の口が呼び出しを取り消した時の印（取り消した答えは、AI へ渡さない）。</param>
        Task<AgentToolReply> InvokeAsync(string name, JToken arguments, CancellationToken cancel);

        /// <summary>
        /// Tells the host that the reply reached the caller.
        /// 答えを AI へ渡し終えた（渡せなかった答え・取り消した答えには呼ばない）。どのスレッドから呼んでもよい。
        /// </summary>
        void Delivered(AgentToolReply reply);
    }

    /// <summary>
    /// The description of one tool: name, text, and input schema.
    /// AI に渡す道具 1 つ（名前・説明・入力の JSON Schema）。
    /// </summary>
    public sealed class AgentToolSpec
    {
        /// <summary>
        /// Creates a tool description.
        /// 道具の説明を作る。
        /// </summary>
        public AgentToolSpec(string name, string description, JObject inputSchema)
        {
            this.Name = string.IsNullOrEmpty(name) ? throw new ArgumentException("道具の名前が空。", nameof(name)) : name;
            this.Description = description ?? string.Empty;
            this.InputSchema = inputSchema ?? throw new ArgumentNullException(nameof(inputSchema));
        }

        /// <summary>
        /// The name of the tool.
        /// 道具の名前。
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The text that explains the tool.
        /// 道具を説明する字。
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// The JSON schema of the tool input.
        /// 入力の形（JSON Schema・⚠ 受け取った側は書き換えない）。
        /// </summary>
        public JObject InputSchema { get; }
    }

    /// <summary>
    /// The reply of one tool run.
    /// 道具の答え 1 つ（AI へ渡す字と、断りかどうか）。
    /// </summary>
    public sealed class AgentToolReply
    {
        /// <summary>
        /// Creates a tool reply.
        /// 道具の返事を作る。
        /// </summary>
        public AgentToolReply(string text, bool isError, object receipt = null)
        {
            this.Text = text ?? string.Empty;
            this.IsError = isError;
            this.Receipt = receipt;
        }

        /// <summary>
        /// The text of the reply.
        /// AI へ渡す字（JSON）。
        /// </summary>
        public string Text { get; }

        /// <summary>
        /// True when the run failed.
        /// 断り・失敗の答えか（AI の口へ「失敗」と伝える）。
        /// </summary>
        public bool IsError { get; }

        /// <summary>
        /// An object that is given back to Delivered.
        /// 受け手の控え（<see cref="IAgentToolHost.Delivered"/> で受け手が読む・AI の口は触らない）。
        /// </summary>
        public object Receipt { get; }
    }
}
