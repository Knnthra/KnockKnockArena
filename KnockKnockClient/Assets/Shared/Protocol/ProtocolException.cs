using System;

namespace KnockKnockArena.Shared.Protocol
{
    public sealed class ProtocolException : Exception
    {
        public string Reason { get; }
        
        public bool IsFatal{get;}
        
        public ProtocolException(string reason, bool isFatal = false) : base(reason)
        {
            Reason = reason;
            IsFatal = isFatal;
        }

    }
}

