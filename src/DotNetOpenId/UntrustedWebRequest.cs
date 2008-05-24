#if DEBUG
#define LONGTIMEOUT
#endif
namespace DotNetOpenId {
	using System;
	using System.Net;
	using System.IO;
using System.Diagnostics;
	using System.Globalization;
using System.Collections.Generic;

	/// <summary>
	/// A paranoid HTTP get/post request engine.  It helps to protect against attacks from remote
	/// server leaving dangling connections, sending too much data, causing requests against 
	/// internal servers, etc.
	/// </summary>
	/// <remarks>
	/// Protections include:
	/// * Conservative maximum time to receive the complete response.
	/// * Only HTTP and HTTPS schemes are permitted.
	/// * Internal IP address ranges are not permitted: 127.*.*.*, 1::*
	/// * Internal host names are not permitted (periods must be found in the host name)
	/// </remarks>
	public static class UntrustedWebRequest {
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		static int maximumBytesToRead = 1024 * 1024;
		/// <summary>
		/// The default maximum bytes to read in any given HTTP request.
		/// Default is 1MB.  Cannot be less than 2KB.
		/// </summary>
		public static int MaximumBytesToRead {
			get { return maximumBytesToRead; }
			set {
				if (value < 2048) throw new ArgumentOutOfRangeException("value");
				maximumBytesToRead = value;
			}
		}
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		static int maximumRedirections = 10;
		/// <summary>
		/// The total number of redirections to allow on any one request.
		/// Default is 10.
		/// </summary>
		public static int MaximumRedirections {
			get { return maximumRedirections; }
			set {
				if (value < 0) throw new ArgumentOutOfRangeException("value");
				maximumRedirections = value;
			}
		}
		/// <summary>
		/// Gets the time allowed to wait for single read or write operation to complete.
		/// Default is 500 milliseconds.
		/// </summary>
		public static TimeSpan ReadWriteTimeout { get; set; }
		/// <summary>
		/// Gets the time allowed for an entire HTTP request.  
		/// Default is 5 seconds.
		/// </summary>
		public static TimeSpan Timeout { get; set; }

		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1810:InitializeReferenceTypeStaticFieldsInline")]
		static UntrustedWebRequest() {
			ReadWriteTimeout = TimeSpan.FromMilliseconds(500);
			Timeout = TimeSpan.FromSeconds(10);
#if LONGTIMEOUT
			ReadWriteTimeout = TimeSpan.FromHours(1);
			Timeout = TimeSpan.FromHours(1);
#endif
		}

		static bool isIPv6Loopback(IPAddress ip) {
			Debug.Assert(ip != null);
			byte[] addressBytes = ip.GetAddressBytes();
			for (int i = 0; i < addressBytes.Length - 1; i++)
				if (addressBytes[i] != 0) return false;
			if (addressBytes[addressBytes.Length - 1] != 1) return false;
			return true;
		}
		static ICollection<string> allowableSchemes = new List<string> { "http", "https" };
		static bool isUriAllowable(Uri uri) {
			Debug.Assert(uri != null);
			if (!allowableSchemes.Contains(uri.Scheme)) {
				if (TraceUtil.Switch.TraceWarning)
					Trace.TraceWarning("Rejecting URL {0} because it uses a disallowed scheme.", uri);
				return false;
			}
			// Try to interpret the hostname as an IP address so we can test for internal
			// IP address ranges.  Note that IP addresses can appear in many forms 
			// (e.g. http://127.0.0.1, http://2130706433, http://0x0100007f, http://::1
			// So we convert them to a canonical IPAddress instance, and test for all
			// non-routable IP ranges: 10.*.*.*, 127.*.*.*, ::1
			// Note that Uri.IsLoopback is very unreliable, not catching many of these variants.
			IPAddress hostIPAddress;
			if (IPAddress.TryParse(uri.DnsSafeHost, out hostIPAddress)) {
				byte[] addressBytes = hostIPAddress.GetAddressBytes();
				// The host is actually an IP address.
				switch (hostIPAddress.AddressFamily) {
					case System.Net.Sockets.AddressFamily.InterNetwork:
						if (addressBytes[0] == 127 || addressBytes[0] == 10) {
							if (TraceUtil.Switch.TraceWarning)
								Trace.TraceWarning("Rejecting URL {0} because it is a loopback address.", uri);
							return false;
						}
						break;
					case System.Net.Sockets.AddressFamily.InterNetworkV6:
						if (isIPv6Loopback(hostIPAddress)) {
							if (TraceUtil.Switch.TraceWarning)
								Trace.TraceWarning("Rejecting URL {0} because it is a loopback address.", uri);
							return false;
						}
						break;
					default:
						if (TraceUtil.Switch.TraceWarning)
							Trace.TraceWarning("Rejecting URL {0} because it does not use an IPv4 or IPv6 address.");
						return false;
				}
			} else {
				// The host is given by name.  We require names to contain periods to
				// help make sure it's not an internal address.
				if (!uri.Host.Contains(".")) {
					if (TraceUtil.Switch.TraceWarning)
						Trace.TraceWarning("Rejecting URL {0} because it does not contain a period in the host name.", uri);
					return false;
				}
			}
			return true;
		}

		/// <summary>
		/// Reads a maximum number of bytes from a response stream.
		/// </summary>
		/// <returns>
		/// The number of bytes actually read.  
		/// WARNING: This can be fewer than the size of the returned buffer.
		/// </returns>
		static void readData(HttpWebResponse resp, out byte[] buffer, out int length) {
			int bufferSize = resp.ContentLength >= 0 && resp.ContentLength < int.MaxValue ?
				Math.Min(MaximumBytesToRead, (int)resp.ContentLength) : MaximumBytesToRead;
			buffer = new byte[bufferSize];
			using (Stream stream = resp.GetResponseStream()) {
				int dataLength = 0;
				int chunkSize;
				while (dataLength < bufferSize && (chunkSize = stream.Read(buffer, dataLength, bufferSize - dataLength)) > 0)
					dataLength += chunkSize;
				length = dataLength;
			}
		}

		static UntrustedWebResponse getResponse(Uri requestUri, HttpWebResponse resp) {
			byte[] data;
			int length;
			readData(resp, out data, out length);
			return new UntrustedWebResponse(requestUri, resp, new MemoryStream(data, 0, length));
		}

		internal static UntrustedWebResponse Request(Uri uri) {
			return Request(uri, null);
		}

		internal static UntrustedWebResponse Request(Uri uri, byte[] body) {
			return Request(uri, body, null);
		}

		internal static UntrustedWebResponse Request(Uri uri, byte[] body, string[] acceptTypes) {
			return Request(uri, body, acceptTypes, false);
		}

		static UntrustedWebResponse Request(Uri uri, byte[] body, string[] acceptTypes,
			bool avoidSendingExpect100Continue) {
			if (uri == null) throw new ArgumentNullException("uri");
			if (!isUriAllowable(uri)) throw new ArgumentException(string.Format(CultureInfo.CurrentCulture,
				Strings.UnsafeWebRequestDetected, uri), "uri");

			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
			request.ReadWriteTimeout = (int)ReadWriteTimeout.TotalMilliseconds;
			request.Timeout = (int)Timeout.TotalMilliseconds;
			request.KeepAlive = false;
			request.MaximumAutomaticRedirections = MaximumRedirections;
			if (acceptTypes != null)
				request.Accept = string.Join(",", acceptTypes);
			if (body != null) {
				request.ContentType = "application/x-www-form-urlencoded";
				request.ContentLength = body.Length;
				request.Method = "POST";
				if (avoidSendingExpect100Continue) {
					// Some OpenID servers doesn't understand Expect header and send 417 error back.
					// If this server just failed from that, we're trying again without sending the
					// "Expect: 100-Continue" HTTP header. (see Google Code Issue 72)
					// We don't just set Expect100Continue = !avoidSendingExpect100Continue
					// so that future requests don't reset this and have to try twice as well.
					// We don't want to blindly set all ServicePoints to not use the Expect header
					// as that would be a security hole allowing any visitor to a web site change
					// the web site's global behavior when calling that host.
					request.ServicePoint.Expect100Continue = false;
				}
			}

			try {
				if (body != null) {
					using (Stream outStream = request.GetRequestStream()) {
						outStream.Write(body, 0, body.Length);
					}
				}

				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) {
					return getResponse(uri, response);
				}
			} catch (WebException e) {
				using (HttpWebResponse response = (HttpWebResponse)e.Response) {
					if (response != null) {
						if (response.StatusCode == HttpStatusCode.ExpectationFailed) {
							if (!avoidSendingExpect100Continue) { // must only try this once more
								return Request(uri, body, acceptTypes, true);
							}
						}
						return getResponse(uri, response);
					} else {
						throw;
					}
				}
			}
		}
	}
}
