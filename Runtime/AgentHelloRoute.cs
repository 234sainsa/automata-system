using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// <c>/v1/hello</c>＝版・開き方・開いた力・どのフォルダか・顔・段・世界の様子・止める反射と手足の様子・使える道・合図の名前・上限を返す。世界ができていなくても答える。
    /// ⭐ 世界を読まない係の道（<see cref="IAgentControlRoute"/>）＝裏のトークンで来ても、裏を読んだ数に数えない。
    /// </summary>
    internal sealed class AgentHelloRoute : IAgentControlRoute
    {
        public string Path => "/v1/hello";

        public AgentFace Face => AgentFace.Surface;

        /// <summary>hello は、どの力でも使える（使う側が、まず開いた力を知るため）。</summary>
        public AgentAbilities Needs => AgentAbilities.None;

        public void Handle(AgentCall call, AgentReplyWriter reply)
        {
            JsonWriter w = reply.Json;
            AgentAbilities abilities = AgentHost.Abilities;

            w.WritePropertyName("open");
            w.WriteValue(AgentHost.OpenWord);
            WriteSortedStrings(w, "abilities", abilities.ToWireList());

            // どの Editor か（ゲームが登録した短い名乗り）。登録が無い・null なら出さない。
            string project = AgentProjectLabel.Read();
            if (project != null)
            {
                w.WritePropertyName("project");
                w.WriteValue(project);
            }

            w.WritePropertyName("face");
            w.WriteValue(call.Face.ToWire());
            w.WritePropertyName("stage");
            w.WriteValue(AgentHost.BuiltStage);

            IAgentWorld world = AgentHost.CurrentWorld;
            w.WritePropertyName("world");
            w.WriteStartObject();
            w.WritePropertyName("state");
            w.WriteValue(AgentHost.WorldState.ToWire());
            w.WritePropertyName("epoch");
            w.WriteValue(AgentHost.Epoch);
            w.WritePropertyName("phase");
            w.WriteValue(world != null ? world.PhaseName : null);
            w.WriteEndObject();

            // 止める反射の様子（腕・残り・守りの名前・掛け金）と手足の様子（借り・走っている命令・命令の名前）。A の手（self）を開いた時だけ。
            AgentHostSession session = AgentHost.CurrentSession;
            if (session != null && abilities.Allows(AgentAbilities.Self))
            {
                AgentReflexJson.Write(w, session.Reflex, AgentSignalHub.NowMs());
                AgentHandsJson.WriteHello(w, session.Hands, AgentReflexJson.ReadyWorld());
            }

            // 使える道＝開いた力で使える道のうち、表のトークンには表の道だけ、裏のトークンには全部を見せる。
            var visible = new List<string>();
            if (session != null)
            {
                foreach (IAgentRoute route in session.AllRoutes)
                {
                    if (route.Face == AgentFace.Surface || call.Face == AgentFace.Back)
                    {
                        visible.Add(route.Path);
                    }
                }
            }

            WriteSortedStrings(w, "routes", visible);

            // 合図の名前＝Host の分と、今の世界の元のうち開いた力でつなぐ元の分と、ゲームの側が名乗った分（表のトークンには表の合図だけ）。
            // ⭐ その顔の帳面を読む道（wait・events）が開いていなければ、何も並べない（読めない合図を見せない）。
            var signals = new HashSet<string>(StringComparer.Ordinal);
            bool canRead = call.Face == AgentFace.Back
                ? session != null && session.Signals.Back != null
                : abilities.Allows(AgentAbilityCatalog.Current.SurfaceSignalReaders);
            if (canRead)
            {
                AddKinds(signals, AgentHost.HostSignalKinds, call.Face);
                if (abilities.Allows(AgentAbilities.Self))
                {
                    AddKinds(signals, AgentHost.ReflexSignalKinds, call.Face);
                    AddKinds(signals, AgentLeaseDesk.SignalKinds, call.Face);
                }

                IReadOnlyList<IAgentSignalSource> sources = world != null ? world.SignalSources : null;
                if (sources != null)
                {
                    foreach (IAgentSignalSource source in sources)
                    {
                        if (source != null && abilities.Allows(source.Needs))
                        {
                            AddKinds(signals, source.Kinds, call.Face);
                        }
                    }
                }

                AgentHost.CollectDeclaredKinds(abilities, call.Face, signals);
            }

            WriteSortedStrings(w, "signals", new List<string>(signals));

            w.WritePropertyName("limits");
            w.WriteStartObject();
            w.WritePropertyName("bodyBytes");
            w.WriteValue(AgentGate.MaxBodyBytes);
            w.WritePropertyName("perSecond");
            w.WriteValue(AgentHost.RequestsPerSecond);
            w.WritePropertyName("callsPerFrame");
            w.WriteValue(AgentHost.MaxCallsPerFrame);
            w.WritePropertyName("mainThreadTimeoutSec");
            w.WriteValue((int)AgentHost.MainThreadTimeout.TotalSeconds);
            w.WritePropertyName("waitMaxSec");
            w.WriteValue(AgentWaitRoute.MaxTimeoutSec);
            w.WritePropertyName("waits");
            w.WriteValue(AgentWaitRoute.MaxHeld);
            w.WritePropertyName("ledger");
            w.WriteValue(AgentSignalLedger.DefaultCapacity);
            w.WritePropertyName("captureGapMs");
            w.WriteValue(AgentCaptureDesk.GapMs);
            w.WritePropertyName("captureKeep");
            w.WriteValue(AgentCaptureDesk.KeepFiles);
            w.WriteEndObject();

            // 裏のトークンには、合図の係の様子も見せる（元をつないでいるか・元を回す重さ）。
            if (call.Face == AgentFace.Back && session != null)
            {
                AgentSignalHub hub = session.Signals;
                w.WritePropertyName("signalStats");
                w.WriteStartObject();
                w.WritePropertyName("attached");
                w.WriteValue(hub.IsAttached);
                w.WritePropertyName("active");
                w.WriteValue(hub.IsActive(AgentSignalHub.NowMs()));
                w.WritePropertyName("lastTickMs");
                w.WriteValue(Math.Round(hub.LastTickMs, 3));
                w.WritePropertyName("maxTickMs");
                w.WriteValue(Math.Round(hub.MaxTickMs, 3));
                w.WritePropertyName("seq");
                w.WriteValue(hub.Surface.LastSeq);
                w.WritePropertyName("bseq");
                w.WriteValue(hub.Back != null ? hub.Back.LastSeq : 0);
                w.WritePropertyName("held");
                w.WriteValue(session.HeldCount);
                w.WriteEndObject();
            }
        }

        private static void AddKinds(HashSet<string> into, IReadOnlyList<AgentSignalKind> kinds, AgentFace face)
        {
            if (kinds == null)
            {
                return;
            }

            foreach (AgentSignalKind kind in kinds)
            {
                if (kind.Name != null && (kind.Face == AgentFace.Surface || face == AgentFace.Back))
                {
                    into.Add(kind.Name);
                }
            }
        }

        private static void WriteSortedStrings(JsonWriter w, string name, List<string> values)
        {
            values.Sort(StringComparer.Ordinal);
            w.WritePropertyName(name);
            w.WriteStartArray();
            foreach (string value in values)
            {
                w.WriteValue(value);
            }

            w.WriteEndArray();
        }
    }
}
