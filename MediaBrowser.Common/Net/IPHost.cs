#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MediaBrowser.Common.Net
{
    /// <summary>
    /// Object that holds a host name.
    /// </summary>
    public class IPHost : IPObject
    {
        /// <summary>
        /// Gets or sets timeout value before resolve required, in minutes.
        /// </summary>
        public const int Timeout = 30;

        /// <summary>
        /// Represents an IPHost that has no value.
        /// </summary>
        public static readonly IPHost None = new IPHost(string.Empty, IPAddress.None);

        /// <summary>
        /// Time when last resolved in ticks.
        /// </summary>
        private DateTime? _lastResolved = null;

        /// <summary>
        /// Gets the IP Addresses, attempting to resolve the name, if there are none.
        /// </summary>
        private IPAddress[] _addresses;

        /// <summary>
        /// Initializes a new instance of the <see cref="IPHost"/> class.
        /// </summary>
        /// <param name="name">Host name to assign.</param>
        public IPHost(string name)
        {
            HostName = name ?? throw new ArgumentNullException(nameof(name));
            _addresses = Array.Empty<IPAddress>();
            Resolved = false;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="IPHost"/> class.
        /// </summary>
        /// <param name="name">Host name to assign.</param>
        /// <param name="address">Address to assign.</param>
        private IPHost(string name, IPAddress address)
        {
            HostName = name ?? throw new ArgumentNullException(nameof(name));
            _addresses = new IPAddress[] { address ?? throw new ArgumentNullException(nameof(address)) };
            Resolved = !address.Equals(IPAddress.None);
        }

        /// <summary>
        /// Gets or sets the object's first IP address.
        /// </summary>
        public override IPAddress Address
        {
            get
            {
                return ResolveHost() ? this[0] : IPAddress.None;
            }

            set
            {
                // Not implemented, as a host's address is determined by DNS.
                throw new NotImplementedException("The address of a host is determined by DNS.");
            }
        }

        /// <summary>
        /// Gets or sets the object's first IP's subnet prefix.
        /// The setter does nothing, but shouldn't raise an exception.
        /// </summary>
        public override byte PrefixLength
        {
            get
            {
                return (byte)(ResolveHost() ? 128 : 32);
            }

            set
            {
                // Not implemented, as a host object can only have a prefix length of 128 (IPv6) or 32 (IPv4) prefix length,
                // which is automatically determined by it's IP type. Anything else is meaningless.
            }
        }

        /// <summary>
        /// Gets a value indicating whether the address has a value.
        /// </summary>
        public bool HasAddress => _addresses.Length != 0;

        /// <summary>
        /// Gets the host name of this object.
        /// </summary>
        public string HostName { get; }

        /// <summary>
        /// Gets a value indicating whether this host has attempted to be resolved.
        /// </summary>
        public bool Resolved { get; private set; }

        /// <summary>
        /// Gets or sets the IP Addresses associated with this object.
        /// </summary>
        /// <param name="index">Index of address.</param>
        public IPAddress this[int index]
        {
            get
            {
                ResolveHost();
                return index >= 0 && index < _addresses.Length ? _addresses[index] : IPAddress.None;
            }
        }

        /// <summary>
        /// Attempts to parse the host string.
        /// </summary>
        /// <param name="host">Host name to parse.</param>
        /// <param name="hostObj">Object representing the string, if it has successfully been parsed.</param>
        /// <returns><c>true</c> if the parsing is successful, <c>false</c> if not.</returns>
        public static bool TryParse(string host, out IPHost hostObj)
        {
            if (!string.IsNullOrEmpty(host))
            {
                // See if it's an IPv6 with port address e.g. [::1]:120.
                int i = host.IndexOf("]:", StringComparison.OrdinalIgnoreCase);
                if (i != -1)
                {
                    return TryParse(host.Remove(i - 1).TrimStart(' ', '['), out hostObj);
                }
                else
                {
                    // See if it's an IPv6 in [] with no port.
                    i = host.IndexOf(']', StringComparison.OrdinalIgnoreCase);
                    if (i != -1)
                    {
                        return TryParse(host.Remove(i - 1).TrimStart(' ', '['), out hostObj);
                    }

                    // Is it a host or IPv4 with port?
                    string[] hosts = host.Split(':');

                    if (hosts.Length > 2)
                    {
                        hostObj = new IPHost(string.Empty, IPAddress.None);
                        return false;
                    }

                    // Remove port from IPv4 if it exists.
                    host = hosts[0];

                    if (string.Equals("localhost", host, StringComparison.OrdinalIgnoreCase))
                    {
                        hostObj = new IPHost(host, new IPAddress(Ipv4Loopback));
                        return true;
                    }

                    if (IPNetAddress.TryParse(host, out IPNetAddress netIP))
                    {
                        // Host name is an ip address, so fake resolve.
                        hostObj = new IPHost(host, netIP.Address);
                        return true;
                    }
                }

                // Only thing left is to see if it's a host string.
                if (!string.IsNullOrEmpty(host))
                {
                    // Use regular expression as CheckHostName isn't RFC5892 compliant.
                    // Modified from gSkinner's expression at https://stackoverflow.com/questions/11809631/fully-qualified-domain-name-validation
                    Regex re = new Regex(@"^(?!:\/\/)(?=.{1,255}$)((.{1,63}\.){0,127}(?![0-9]*$)[a-z0-9-]+\.?)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    if (re.Match(host).Success)
                    {
                        hostObj = new IPHost(host);
                        return true;
                    }
                }
            }

            hostObj = IPHost.None;
            return false;
        }

        /// <summary>
        /// Attempts to parse the host string.
        /// </summary>
        /// <param name="host">Host name to parse.</param>
        /// <returns>Object representing the string, if it has successfully been parsed.</returns>
        public static IPHost Parse(string host)
        {
            if (!string.IsNullOrEmpty(host) && IPHost.TryParse(host, out IPHost res))
            {
                return res;
            }

            throw new InvalidCastException("Host does not contain a valid value. {host}");
        }

        /// <summary>
        /// Attempts to parse the host string, ensuring that it resolves only to a specific IP type.
        /// </summary>
        /// <param name="host">Host name to parse.</param>
        /// <param name="family">Addressfamily filter.</param>
        /// <returns>Object representing the string, if it has successfully been parsed.</returns>
        public static IPHost Parse(string host, AddressFamily family)
        {
            if (!string.IsNullOrEmpty(host) && IPHost.TryParse(host, out IPHost res))
            {
                if (family == AddressFamily.InterNetwork)
                {
                    res.Remove(AddressFamily.InterNetworkV6);
                }
                else
                {
                    res.Remove(AddressFamily.InterNetwork);
                }

                return res;
            }

            throw new InvalidCastException("Host does not contain a valid value. {host}");
        }

        /// <summary>
        /// Returns the Addresses that this item resolved to.
        /// </summary>
        /// <returns>IPAddress Array.</returns>
        public IPAddress[] GetAddresses()
        {
            ResolveHost();
            return _addresses;
        }

        /// <inheritdoc/>
        public override bool Contains(IPAddress address)
        {
            if (address != null && !Address.Equals(IPAddress.None))
            {
                if (address.IsIPv4MappedToIPv6)
                {
                    address = address.MapToIPv4();
                }

                foreach (var addr in GetAddresses())
                {
                    if (address.Equals(addr))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <inheritdoc/>
        public override bool Equals(IPObject? other)
        {
            if (other is IPHost otherObj)
            {
                // Do we have the name Hostname?
                if (string.Equals(otherObj.HostName, HostName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!ResolveHost() || !otherObj.ResolveHost())
                {
                    return false;
                }

                // Do any of our IP addresses match?
                foreach (IPAddress addr in _addresses)
                {
                    foreach (IPAddress otherAddress in otherObj._addresses)
                    {
                        if (addr.Equals(otherAddress))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <inheritdoc/>
        public override bool IsIP6()
        {
            // Returns true if interfaces are only IP6.
            if (ResolveHost())
            {
                foreach (IPAddress i in _addresses)
                {
                    if (i.AddressFamily != AddressFamily.InterNetworkV6)
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            // StringBuilder not optimum here.
            string output = string.Empty;
            if (_addresses.Length > 0)
            {
                bool moreThanOne = _addresses.Length > 1;
                if (moreThanOne)
                {
                    output = "[";
                }

                foreach (var i in _addresses)
                {
                    if (Address.Equals(IPAddress.None) && Address.AddressFamily == AddressFamily.Unspecified)
                    {
                        output += HostName + ",";
                    }
                    else if (i.Equals(IPAddress.Any))
                    {
                        output += "Any IP4 Address,";
                    }
                    else if (Address.Equals(IPAddress.IPv6Any))
                    {
                        output += "Any IP6 Address,";
                    }
                    else if (i.Equals(IPAddress.Broadcast))
                    {
                        output += "Any Address,";
                    }
                    else
                    {
                        output += $"{i}/32,";
                    }
                }

                output = output[0..^1];

                if (moreThanOne)
                {
                    output += "]";
                }
            }
            else
            {
                output = HostName;
            }

            return output;
        }

        /// <inheritdoc/>
        public override void Remove(AddressFamily family)
        {
            if (ResolveHost())
            {
                _addresses = _addresses.Where(p => p.AddressFamily != family).ToArray();
            }
        }

        /// <inheritdoc/>
        public override bool Contains(IPObject address)
        {
            // An IPHost cannot contain another IPObject, it can only be equal.
            return Equals(address);
        }

        /// <inheritdoc/>
        protected override IPObject CalculateNetworkAddress()
        {
            var netAddr = NetworkAddressOf(this[0], PrefixLength);
            return new IPNetAddress(netAddr.Address, netAddr.PrefixLength);
        }

        /// <summary>
        /// Attempt to resolve the ip address of a host.
        /// </summary>
        /// <returns><c>true</c> if any addresses have been resolved, otherwise <c>false</c>.</returns>
        private bool ResolveHost()
        {
            // When was the last time we resolved?
            if (_lastResolved == null)
            {
                _lastResolved = DateTime.UtcNow;
            }

            // If we haven't resolved before, or our timer has run out...
            if ((_addresses.Length == 0 && !Resolved) || (DateTime.UtcNow > _lastResolved?.AddMinutes(Timeout)))
            {
                _lastResolved = DateTime.UtcNow;
                ResolveHostInternal().GetAwaiter().GetResult();
                Resolved = true;
            }

            return _addresses.Length > 0;
        }

        /// <summary>
        /// Task that looks up a Host name and returns its IP addresses.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task ResolveHostInternal()
        {
            if (!string.IsNullOrEmpty(HostName))
            {
                // Resolves the host name - so save a DNS lookup.
                if (string.Equals(HostName, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    _addresses = new IPAddress[] { new IPAddress(Ipv4Loopback), new IPAddress(Ipv6Loopback) };
                    return;
                }

                if (Uri.CheckHostName(HostName).Equals(UriHostNameType.Dns))
                {
                    try
                    {
                        IPHostEntry ip = await Dns.GetHostEntryAsync(HostName).ConfigureAwait(false);
                        _addresses = ip.AddressList;
                    }
                    catch (SocketException ex)
                    {
                        // Log and then ignore socket errors, as the result value will just be an empty array.
                        Debug.WriteLine("GetHostEntryAsync failed with {Message}.", ex.Message);
                    }
                }
            }
        }
    }
}
