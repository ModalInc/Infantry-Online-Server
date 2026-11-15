using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Network;
using InfServer.Protocol;

namespace InfServer.Logic
{	// Logic_Reliable Class
	/// Deals with handling and keeping track of reliable packets
	///////////////////////////////////////////////////////
	class Logic_Reliable
	{	/// <summary>a is after b in 16-bit sequence space (wraps)</summary>
		static bool Ahead(ushort a, ushort b) { return a != b && (ushort)(a - b) < 0x8000; }

		/// <summary>
		/// Handles all reliable packets received from clients
		/// </summary>
		static public void Handle_Reliable(Reliable pkt, Client client)
		{	//Get the relevant stream
			Client.StreamState stream = client._streams[pkt.streamID];
			
			//Is the reliable number what we expected? (wrap-aware - plain > / < broke once the client's 16-bit
			// counter wrapped past 65,535: 2026-11-15)
			if (Ahead(pkt.rNumber, stream.C2S_Reliable))
			{	//Report it!
                Log.write(TLog.Inane, "OOS Reliable Packet Stream[{0}]. {1} vs {2} from {3}", pkt.streamID, pkt.rNumber, stream.C2S_Reliable, client._connectionID);
				client.reportOutOfSync(pkt, pkt.rNumber, pkt.streamID);
				return;
			}
			//A previously received reliable?
			else if (Ahead(stream.C2S_Reliable, pkt.rNumber))
			{	//Re-send the echo
				ReliableEcho resent = new ReliableEcho(pkt.streamID);
				resent.rNumber = (ushort)(stream.C2S_Reliable - 1);
				client.send(resent);
				return;
			}

			//Expect the next!
			stream.C2S_Reliable++;

			//Handle the message we're supposed to receive
			client._handler.handlePacket(pkt.packet, client);

			//If we have other packets in seqence waiting in store, use them too
			PacketBase unhandled;
			ushort reliableNext = stream.C2S_Reliable;

			while (stream.oosReliable.TryGetValue(reliableNext, out unhandled))
			{	//Handle it and go to next packet
				client._handler.handlePacket(unhandled, client);
				stream.oosReliable.Remove(reliableNext++);
			}

			//Prepare an echo for all received packets
			ReliableEcho echo = new ReliableEcho(pkt.streamID);
			echo.rNumber = (ushort)(stream.C2S_Reliable - 1);
			client.send(echo);
		}

		/// <summary>
		/// Handles all reliable confirmations received from clients
		/// </summary>
		static public void Handle_ReliableEcho(ReliableEcho pkt, Client client)
		{	//Confirm that it was received
			client.confirmReliable(pkt.rNumber, pkt.streamID);
		}

		/// <summary>
		/// Handles all out-of-sync notifications
		/// </summary>
		static public void Handle_OutOfSync(OutOfSync pkt, Client client)
		{
			// Fill the gap at once: resend every unconfirmed reliable up to the reported one (local fix 2026-11-14).
			// (2026-11-15: no fallback to resending #rNumber itself - the client reports the one it got OUT OF ORDER, so
			//  it already has it; resending it on every repeat report was pure duplicate traffic. When nothing was due
			//  (all sent < 300 ms ago) the regular resend timer covers the gap.)
			if (client.resendReliableUpTo(pkt.streamID, pkt.rNumber))
				return;

			// Nothing left to resend (already confirmed / gone) - normal, the client catches up by itself.
			// (Was a warning on every report; it is not an error.)
		}

		/// <summary>
		/// Registers all handlers
		/// </summary>
		[Logic.RegistryFunc]
		static public void Register()
		{
			Reliable.Handlers += Handle_Reliable;
			ReliableEcho.Handlers += Handle_ReliableEcho;
			OutOfSync.Handlers += Handle_OutOfSync;
		}
	}
}
