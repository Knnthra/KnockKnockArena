using System.Net;
using System.Net.Sockets;

namespace KnockKnockArena.Shared.Protocol
{
    public static class AddressResolver
    {   
        
        public static IPAddress Resolve(string host)
        {
            if(TryResolve(host, out IPAddress address, out string error))
                return address;
                
            throw new ProtocolException(error);
        }
        
        public static bool TryResolve(string host, out IPAddress address, out string error)
        {
            address = IPAddress.None;
            error = "";

            string trimmed = (host ?? "").Trim();
            if (trimmed.Length == 0)
            {
                error = "no host given";
                return false;
            }

            if (IPAddress.TryParse(trimmed, out IPAddress? literal))
            {
                if (literal.AddressFamily != AddressFamily.InterNetwork)
                {
                    error = $"'{trimmed}' is an IPv6 address; the server listens on IPv4";
                    return false;
                }

                address = literal;
                return true;
            }

            IPAddress[] candidates;

            try
            {
                candidates = Dns.GetHostAddresses(trimmed);
            }
            catch (SocketException ex)
            {
                error = ex.SocketErrorCode == SocketError.HostNotFound
    ? $"unknown host '{trimmed}' (DNS has no address for it)"
    : $"could not resolve '{trimmed}': {ex.Message}";
                return false;
            }

            foreach (IPAddress candidate in candidates)
            {
                if (candidate.AddressFamily == AddressFamily.InterNetwork)
                {
                    address = candidate;
                    return true;
                }
            }
            error = $"'{trimmed}' has no IPv4 address (only {candidates.Length} IPv6)";
            return false;

        }
    }
}