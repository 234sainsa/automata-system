using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem.Tests
{
    /// <summary>
    /// Tests for the hands lease and command desk.
    /// 手足の借りと命令の係の試験。この試験は、実機で作りにくい物だけを見る（人の本物の入力・黙った長さ・送り直し・邪魔・締め切り）。
    /// </summary>
    public sealed class AgentLeaseDeskTests
    {
        private const string Me = "claude-test";

        [Test]
        public void HumanInputCutsLeaseAndActDoesNotRetakeIt()
        {
            var (desk, hands, _, t0) = Make();
            Assert.IsNull(desk.TryTake(Me, t0, true, hands));

            hands.Human = true;
            desk.WatchHuman(t0 + 100, hands);

            Assert.IsNull(desk.LeaseId);
            Assert.AreEqual(AgentLeaseDesk.WhyHuman, desk.LostWhy);
            Assert.AreEqual(1, hands.Ends);

            // 命令のついでには取り直さない（/v1/lease で取り直す）。人が触った直後は /v1/lease でも取れない。
            Assert.AreEqual("lease_lost", desk.ResolveForAct(Me, null, t0 + 200, true, hands, out string hint));
            Assert.AreEqual(AgentLeaseDesk.WhyHuman, hint);
            Assert.AreEqual("human_active", desk.TryTake(Me, t0 + 200, true, hands));
            Assert.IsNull(desk.TryTake(Me, t0 + 100 + AgentLeaseDesk.HumanQuietMs, true, hands));
        }

        [Test]
        public void HumanCutReflexLeaseIsNotRetakenByAnyActName()
        {
            var (desk, hands, command, t0) = Make();
            Assert.IsNull(desk.TryStartReflex("flee", command, new JObject(), t0, hands, null, out _));
            Assert.AreEqual(AgentLeaseDesk.ReflexHolder, desk.Holder);

            hands.Human = true;
            desk.WatchHuman(t0 + 100, hands);
            Assert.IsNull(desk.LeaseId);

            // 人が触って 5 秒たっても、反射の借りを切った後は、どの名前の act も取り直さない（/v1/lease で取り直す）。
            long later = t0 + 100 + AgentLeaseDesk.HumanQuietMs + 1;
            Assert.AreEqual("lease_lost", desk.ResolveForAct(Me, null, later, true, hands, out string hint));
            Assert.AreEqual(AgentLeaseDesk.WhyHuman, hint);
            Assert.AreEqual("human_active", desk.WhyNotReflex(later, hands));
        }

        [Test]
        public void HumanTouchWithoutLeaseKeepsQuietWindowFromLastTouch()
        {
            var (desk, hands, _, t0) = Make();

            // 借りが無い間も、人が触り続けている間は、最後に触った時から 5 秒、だれにも貸さない。
            hands.Human = true;
            desk.WatchHuman(t0, hands);
            hands.Human = true;
            desk.WatchHuman(t0 + 4_000, hands);

            Assert.AreEqual("human_active", desk.TryTake(Me, t0 + AgentLeaseDesk.HumanQuietMs + 1, true, hands));
            Assert.IsNull(desk.TryTake(Me, t0 + 4_000 + AgentLeaseDesk.HumanQuietMs, true, hands));
        }

        [Test]
        public void SameRefFromOtherNameStartsNewAct()
        {
            var (desk, hands, command, t0) = Make();
            desk.TryTake(Me, t0, true, hands);
            desk.TryStart(Me, command, new JObject(), "r1", t0, out _);
            command.Finish(AgentActResult.Done(AgentActResult.ViaPad));

            Assert.IsNull(desk.FindByRef("someone-else", "r1", command.Name, out AgentActTicket seen));
            Assert.IsNull(seen);
        }

        [Test]
        public void IdleReleasesOnlyWhileNoActRuns()
        {
            var (desk, hands, command, t0) = Make();
            Assert.IsNull(desk.TryTake(Me, t0, true, hands));
            Assert.IsNull(desk.TryStart(Me, command, new JObject(), "r1", t0, out AgentActTicket ticket));

            // 命令が走っている間は、黙っていても切らない。
            desk.Tick(t0 + AgentLeaseDesk.IdleMs + 1);
            Assert.IsNotNull(desk.LeaseId);

            command.Finish(AgentActResult.Done(AgentActResult.ViaPad));
            Assert.IsTrue(ticket.Done);

            // 黙った長さは、命令が終わった時から数える。
            long ended = AgentSignalHub.NowMs();
            desk.Tick(ended + AgentLeaseDesk.IdleMs - 100);
            Assert.IsNotNull(desk.LeaseId);
            desk.Tick(ended + AgentLeaseDesk.IdleMs + 100);
            Assert.IsNull(desk.LeaseId);
            Assert.AreEqual("idle", desk.LostWhy);

            // 黙って切れた後は、命令のついでに取り直せる。
            Assert.IsNull(desk.ResolveForAct(Me, null, ended + AgentLeaseDesk.IdleMs + 200, true, hands, out _));
        }

        [Test]
        public void SameRefReturnsFirstTicketWithoutPressingAgain()
        {
            var (desk, hands, command, t0) = Make();
            desk.TryTake(Me, t0, true, hands);
            desk.TryStart(Me, command, new JObject(), "r1", t0, out AgentActTicket first);
            command.Finish(AgentActResult.Done(AgentActResult.ViaPad));

            Assert.IsNull(desk.FindByRef(Me, "r1", command.Name, out AgentActTicket seen));
            Assert.AreSame(first, seen);
            Assert.AreEqual(1, command.Runs);
            Assert.AreEqual("ref_reused", desk.FindByRef(Me, "r1", "other", out _));
        }

        [Test]
        public void SecondActWhileRunningIsBusy()
        {
            var (desk, hands, command, t0) = Make();
            desk.TryTake(Me, t0, true, hands);
            desk.TryStart(Me, command, new JObject(), "r1", t0, out _);

            Assert.AreEqual("busy", desk.TryStart(Me, command, new JObject(), "r2", t0, out _));
        }

        [Test]
        public void InterruptEndsRunningActAndIgnoresLateResult()
        {
            var (desk, hands, command, t0) = Make();
            desk.TryTake(Me, t0, true, hands);
            desk.TryStart(Me, command, new JObject(), "r1", t0, out AgentActTicket ticket);
            int neutralBefore = hands.Neutrals;

            desk.Interrupt("reflex.tripped", 42);

            Assert.IsTrue(ticket.Done);
            Assert.AreEqual(AgentOutcome.Interrupted, ticket.Result.Outcome);
            Assert.AreEqual("reflex.tripped", ticket.ByKind);
            Assert.AreEqual(42, ticket.BySeq);
            Assert.IsTrue(command.Cancel.IsCancellationRequested);
            Assert.Greater(hands.Neutrals, neutralBefore);

            // 遅れて届いた命令の結果は使わない。
            command.Finish(AgentActResult.Done(AgentActResult.ViaPad));
            Assert.AreEqual(AgentOutcome.Interrupted, ticket.Result.Outcome);
        }

        [Test]
        public void DeadlineEndsActAsTimeout()
        {
            var (desk, hands, command, t0) = Make();
            desk.TryTake(Me, t0, true, hands);
            command.Seconds = 1f;
            desk.TryStart(Me, command, new JObject(), null, t0, out AgentActTicket ticket);

            desk.Tick(t0 + 500);
            Assert.IsFalse(ticket.Done);
            desk.Tick(t0 + 1001);
            Assert.IsTrue(ticket.Done);
            Assert.AreEqual(AgentOutcome.Timeout, ticket.Result.Outcome);
            Assert.IsTrue(command.Cancel.IsCancellationRequested);
        }

        [Test]
        public void OtherNameCannotTakeHeldLease()
        {
            var (desk, hands, _, t0) = Make();
            desk.TryTake(Me, t0, true, hands);

            Assert.AreEqual("leased", desk.ResolveForAct("someone-else", null, t0, true, hands, out string hint));
            Assert.AreEqual("other_holder", hint);
            Assert.AreEqual(1, hands.Begins);
        }

        private static (AgentLeaseDesk desk, FakeHands hands, FakeCommand command, long t0) Make()
        {
            var command = new FakeCommand();
            var hands = new FakeHands(command);
            var desk = new AgentLeaseDesk(new AgentSignalHub(false, AgentAbilitiesExtensions.All));
            return (desk, hands, command, AgentSignalHub.NowMs());
        }

        private sealed class FakeHands : IAgentHands
        {
            public FakeHands(IAgentCommand command)
            {
                this.Commands = new List<IAgentCommand> { command };
            }

            public IReadOnlyList<IAgentCommand> Commands { get; }

            public bool Human { get; set; }

            public int Begins { get; private set; }

            public int Neutrals { get; private set; }

            public int Ends { get; private set; }

            public void Begin() => this.Begins++;

            public void Neutral() => this.Neutrals++;

            public void End() => this.Ends++;

            public bool TakeHumanInput()
            {
                bool human = this.Human;
                this.Human = false;
                return human;
            }
        }

        private sealed class FakeCommand : IAgentCommand
        {
            private AwaitableCompletionSource<AgentActResult> pending;

            public string Name => "fake";

            public bool ChangesWorld => false;

            public float Seconds { get; set; } = 10f;

            public int Runs { get; private set; }

            public CancellationToken Cancel { get; private set; }

            public float MaxSeconds(JObject args) => this.Seconds;

            public Awaitable<AgentActResult> RunAsync(JObject args, CancellationToken cancel)
            {
                this.Runs++;
                this.Cancel = cancel;
                this.pending = new AwaitableCompletionSource<AgentActResult>();
                return this.pending.Awaitable;
            }

            public void Finish(AgentActResult result) => this.pending.TrySetResult(result);
        }
    }
}
