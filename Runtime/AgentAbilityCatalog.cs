using System;
using System.Collections.Generic;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The register of abilities: the built-in ones and the ones a game adds.
    /// 力の書き付けの登録簿（開く印の語の読み・hello の abilities の字・つなぎ先のファイル名・表の合図を読める力）。
    /// <para>
    /// ⭐ 土台が持つのは Look・Self・Back と、全部を開く語 <see cref="AllWord"/> だけ。ゲームは自分の力を <see cref="Register"/> で足す。
    /// ⭐ 「全部」（<see cref="All"/>）は、登録した力を全部含む＝operate で、ゲームの力も開く。
    /// ⭐ 窓口が使うのは <see cref="Current"/>。テストは自分で <c>new AgentAbilityCatalog()</c> を作り、ほかの登録に引きずられない。
    /// ⚠ Domain Reload を切っているので、<see cref="Current"/> は Play の頭（SubsystemRegistration）で土台の 3 つだけに戻る（Host が戻す）。
    ///    ゲームは、その後（AfterAssembliesLoaded か BeforeSceneLoad）に登録し直す。窓口が印を読むのは AfterSceneLoad＝登録の後。
    /// </para>
    /// </summary>
    public sealed class AgentAbilityCatalog
    {
        /// <summary>
        /// The word that opens every registered ability.
        /// 全部を開く語（印 "operate"）。
        /// </summary>
        public const string AllWord = "operate";

        /// <summary>
        /// The built-in ability that opens the surface eyes and the back (look).
        /// A の表の目。印 look は、表の目と裏を開く（前からの「見るだけ」と同じ）。
        /// </summary>
        public static readonly AgentAbilityDef LookDef = new AgentAbilityDef(
            "look", AgentAbilities.Look, AgentAbilities.Look | AgentAbilities.Back, readsSurfaceSignals: true);

        /// <summary>
        /// The built-in ability for the hands: lease, act, reflexes, and capture.
        /// A の手。
        /// </summary>
        public static readonly AgentAbilityDef SelfDef = new AgentAbilityDef("self", AgentAbilities.Self);

        /// <summary>
        /// The built-in ability for the back token and the back routes.
        /// 裏のトークンと裏の道。
        /// </summary>
        public static readonly AgentAbilityDef BackDef = new AgentAbilityDef("back", AgentAbilities.Back);

        private static readonly AgentAbilityDef[] BuiltIns = { LookDef, SelfDef, BackDef };

        private readonly object gate = new object();

        /// <summary>登録した書き付け（登録の順）。書き換えは丸ごと差し替える＝読む側はロック無しで読める。</summary>
        private volatile AgentAbilityDef[] defs = BuiltIns;

        /// <summary>
        /// The register that is in use now.
        /// 窓口が使う登録簿。
        /// </summary>
        public static AgentAbilityCatalog Current { get; } = new AgentAbilityCatalog();

        /// <summary>
        /// All registered ability definitions.
        /// 登録した書き付け（土台の 3 つが先・その後はゲームが登録した順）。
        /// </summary>
        public IReadOnlyList<AgentAbilityDef> Defs => this.defs;

        /// <summary>
        /// Every ability flag that is registered.
        /// 全部の力（印 operate）。
        /// </summary>
        public AgentAbilities All
        {
            get
            {
                AgentAbilities all = AgentAbilities.None;
                foreach (AgentAbilityDef def in this.defs)
                {
                    all |= def.Flag;
                }

                return all;
            }
        }

        /// <summary>
        /// The abilities that read surface signals.
        /// 表の合図の道（events・wait）と、hello の合図の名前を使える力（どれか 1 つがあれば使える）。
        /// </summary>
        public AgentAbilities SurfaceSignalReaders
        {
            get
            {
                AgentAbilities readers = AgentAbilities.None;
                foreach (AgentAbilityDef def in this.defs)
                {
                    if (def.ReadsSurfaceSignals)
                    {
                        readers |= def.Flag;
                    }
                }

                return readers;
            }
        }

        /// <summary>
        /// Adds an ability definition to the register.
        /// 力を 1 つ登録する。同じ中身をもう一度登録しても 1 つに数える（Editor の読み込みと Play の頭の両方で呼んでよい）。
        /// ⚠ 同じ名前で中身が違う・位がほかの力と重なる時は、例外で知らせる（黙って片方を捨てない）。
        /// </summary>
        public void Register(AgentAbilityDef def)
        {
            if (def == null)
            {
                throw new ArgumentNullException(nameof(def));
            }

            lock (this.gate)
            {
                AgentAbilityDef[] now = this.defs;
                foreach (AgentAbilityDef old in now)
                {
                    if (old.SameAs(def))
                    {
                        return;
                    }

                    if (old.Name == def.Name)
                    {
                        throw new InvalidOperationException("同じ名前の力が、違う中身で登録済み: " + def.Name);
                    }

                    if ((old.Flag & def.Flag) != AgentAbilities.None)
                    {
                        throw new InvalidOperationException(
                            "力の位がほかの力と重なる: " + def.Name + " と " + old.Name + "（位=" + (int)def.Flag + "）");
                    }

                    if (def.SoloEndpointFileName != null
                        && string.Equals(old.SoloEndpointFileName, def.SoloEndpointFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "専用のつなぎ先の名前が、ほかの力と同じ: " + def.Name + " と " + old.Name + "（" + def.SoloEndpointFileName + "）");
                    }
                }

                var next = new AgentAbilityDef[now.Length + 1];
                Array.Copy(now, next, now.Length);
                next[now.Length] = def;
                this.defs = next;
            }
        }

        /// <summary>
        /// Drops everything a game registered and goes back to the built-in abilities.
        /// 土台の 3 つだけに戻す（Play の頭で Host が呼ぶ・テスト用）。
        /// </summary>
        public void ResetToBuiltIns()
        {
            lock (this.gate)
            {
                this.defs = BuiltIns;
            }
        }

        /// <summary>
        /// Tries to read one word as an ability set.
        /// 印の語 1 つを力の組にする（大文字と小文字は見ない）。知らない語なら false。
        /// operate＝全部／登録した力の名前＝その書き付けの <see cref="AgentAbilityDef.Opens"/>。
        /// </summary>
        public bool TryParseWord(string word, out AgentAbilities abilities)
        {
            string w = (word ?? string.Empty).Trim().ToLowerInvariant();
            if (w == AllWord)
            {
                abilities = this.All;
                return true;
            }

            foreach (AgentAbilityDef def in this.defs)
            {
                if (def.Name == w)
                {
                    abilities = def.Opens;
                    return true;
                }
            }

            abilities = AgentAbilities.None;
            return false;
        }

        /// <summary>
        /// Returns the words that can be written in the open flag.
        /// 読める語の並び（知らせの字に使う・operate が先・その後は登録の順）。
        /// </summary>
        public List<string> Words()
        {
            AgentAbilityDef[] now = this.defs;
            var words = new List<string>(now.Length + 1) { AllWord };
            foreach (AgentAbilityDef def in now)
            {
                words.Add(def.Name);
            }

            return words;
        }

        /// <summary>
        /// Turns an ability set into the sorted list of words that is sent over the wire.
        /// 窓口の JSON に書く字の並び（名前の順・例 ["back","look","self"]）。登録の無い位は書かない。
        /// </summary>
        public List<string> ToWireList(AgentAbilities set)
        {
            AgentAbilityDef[] now = this.defs;
            var words = new List<string>(now.Length);
            foreach (AgentAbilityDef def in now)
            {
                if ((set & def.Flag) != AgentAbilities.None)
                {
                    words.Add(def.Name);
                }
            }

            words.Sort(StringComparer.Ordinal);
            return words;
        }

        /// <summary>
        /// Returns the endpoint file name for an ability set.
        /// この組で開いた窓口のつなぎ先のファイル名。専用のつなぎ先を持つ力 1 つだけを開いた時はその名前、ほかはふだんの名前（endpoint.json）。
        /// </summary>
        public string EndpointFileNameFor(AgentAbilities set)
        {
            AgentAbilityDef solo = this.SoloOwnerOf(set);
            return solo != null ? solo.SoloEndpointFileName : AgentEndpointFile.DefaultFileName;
        }

        /// <summary>
        /// Returns true when the set gets its own endpoint file.
        /// この組が、専用のつなぎ先を持つ力 1 つだけか（その窓口は、本のセッションが別の AI に預けて打たせる物）。
        /// </summary>
        public bool IsSoloEndpoint(AgentAbilities set) => this.SoloOwnerOf(set) != null;

        /// <summary>
        /// Returns every endpoint file name that can be written.
        /// つなぎ先のファイル名の全部（ふだんの名前と、専用の名前）。
        /// </summary>
        public List<string> EndpointFileNames()
        {
            AgentAbilityDef[] now = this.defs;
            var names = new List<string>(now.Length + 1) { AgentEndpointFile.DefaultFileName };
            foreach (AgentAbilityDef def in now)
            {
                if (def.SoloEndpointFileName != null)
                {
                    names.Add(def.SoloEndpointFileName);
                }
            }

            return names;
        }

        private AgentAbilityDef SoloOwnerOf(AgentAbilities set)
        {
            foreach (AgentAbilityDef def in this.defs)
            {
                if (def.SoloEndpointFileName != null && set == def.Flag)
                {
                    return def;
                }
            }

            return null;
        }
    }
}
