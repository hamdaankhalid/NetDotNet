using PChecker;
using PChecker.Runtime;
using PChecker.Runtime.StateMachines;
using PChecker.Runtime.Events;
using PChecker.Runtime.Exceptions;
using PChecker.Runtime.Logging;
using PChecker.Runtime.Values;
using PChecker.Runtime.Specifications;
using Monitor = PChecker.Runtime.Specifications.Monitor;
using System;
using PChecker.SystematicTesting;
using System.Runtime;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable 162, 219, 414, 1998
namespace PImplementation
{
}
namespace PImplementation
{
    public static class GlobalConfig
    {
    }
}
namespace PImplementation
{
    internal partial class Initiator : StateMachine
    {
        private PInt _lastSentSeq = ((PInt)0);
        private PInt _lastPeerSeq = ((PInt)0);
        public class ConstructorEvent : Event{public ConstructorEvent(IPValue val) : base(val) { }}
        
        protected override Event GetConstructorEvent(IPValue value) { return new ConstructorEvent((IPValue)value); }
        public Initiator() {
            this.sends.Add(nameof(PHalt));
            this.receives.Add(nameof(PHalt));
        }
        
        public void Anon(Event currentMachine_dequeuedEvent)
        {
            Initiator currentMachine = this;
            IPValue payload = (IPValue)(gotoPayload ?? ((Event)currentMachine_dequeuedEvent).Payload);
            this.gotoPayload = null;
            _lastSentSeq = (PInt)(((PInt)(0)));
            _lastPeerSeq = (PInt)(((PInt)(0)));
            currentMachine.RaiseGotoStateEvent<SendSyn>();
            return;
        }
        [Start]
        [OnEntry(nameof(Anon))]
        class Init : State
        {
        }
        class SendSyn : State
        {
        }
        class WaitingForSynAck : State
        {
        }
        class SendAck : State
        {
        }
        class EstablishedForInitiator : State
        {
        }
    }
}
namespace PImplementation
{
    internal partial class Responder : StateMachine
    {
        private PInt _lastSentSeq_1 = ((PInt)0);
        private PInt _lastPeerSeq_1 = ((PInt)0);
        public class ConstructorEvent : Event{public ConstructorEvent(IPValue val) : base(val) { }}
        
        protected override Event GetConstructorEvent(IPValue value) { return new ConstructorEvent((IPValue)value); }
        public Responder() {
            this.sends.Add(nameof(PHalt));
            this.receives.Add(nameof(PHalt));
        }
        
        public void Anon_1(Event currentMachine_dequeuedEvent)
        {
            Responder currentMachine = this;
            IPValue payload_1 = (IPValue)(gotoPayload ?? ((Event)currentMachine_dequeuedEvent).Payload);
            this.gotoPayload = null;
            _lastSentSeq_1 = (PInt)(((PInt)(0)));
            _lastPeerSeq_1 = (PInt)(((PInt)(0)));
            currentMachine.RaiseGotoStateEvent<WaitingForSyn>();
            return;
        }
        [Start]
        [OnEntry(nameof(Anon_1))]
        class Init : State
        {
        }
        class WaitingForSyn : State
        {
        }
        class SendSynAck : State
        {
        }
        class WaitingForAck : State
        {
        }
        class EstablishedForResponder : State
        {
        }
    }
}
namespace PImplementation
{
    public class I_Initiator : PMachineValue {
        public I_Initiator (StateMachineId machine, List<string> permissions) : base(machine, permissions) { }
    }
    
    public class I_Responder : PMachineValue {
        public I_Responder (StateMachineId machine, List<string> permissions) : base(machine, permissions) { }
    }
    
    public partial class PHelper {
        public static void InitializeInterfaces() {
            PInterfaces.Clear();
            PInterfaces.AddInterface(nameof(I_Initiator), nameof(PHalt));
            PInterfaces.AddInterface(nameof(I_Responder), nameof(PHalt));
        }
    }
    
}
namespace PImplementation
{
    public partial class PHelper {
        public static void InitializeEnums() {
            PEnum.Clear();
            PEnum.AddEnumElements(new [] {"Bullet","Syn","Ack","WaitingForSynAck"}, new [] {0,1,2,3});
            PEnum.AddEnumElements(new [] {"Initial","SynAck","WaitingForAck"}, new [] {0,1,2});
        }
    }
    
}
#pragma warning restore 162, 219, 414
