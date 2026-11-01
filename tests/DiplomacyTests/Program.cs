using System;
using System.IO;
using RimCoopMod.Networking;

static class Program
{
    static int _failed;

    static void Check(string name, bool ok)
    {
        if (!ok) { _failed++; Console.WriteLine("FAIL " + name); }
    }

    static DiplomacyTable Allied(params string[] pairs)
    {
        var t = new DiplomacyTable();
        for (int i = 0; i < pairs.Length; i += 2) { t.ProposeAlliance(pairs[i], pairs[i + 1]); t.ProposeAlliance(pairs[i + 1], pairs[i]); }
        return t;
    }

    static int Main()
    {
        {
            var t = new DiplomacyTable();
            Check("Neutral_ByDefault", t.Get("a", "b", 0) == Relation.Neutral);
        }
        {
            var t = new DiplomacyTable();
            Check("Alliance_NeedsBoth.propose", t.ProposeAlliance("a", "b") == null);
            Check("Alliance_NeedsBoth.pending", t.Get("a", "b", 0) == Relation.Neutral);
            Check("Alliance_NeedsBoth.accept", t.ProposeAlliance("b", "a") == null);
            Check("Alliance_NeedsBoth.ally", t.Get("a", "b", 0) == Relation.Ally && t.Get("b", "a", 0) == Relation.Ally);
        }
        {
            var t = Allied("a", "b");
            t.ProposeAlliance("a", "b"); // repetir una vez ya aliados no duplica nada
            Check("Alliance_CrossedProposals_Once", t.Get("a", "b", 0) == Relation.Ally && t.AllyCount("a") == 1 && t.AllyCount("b") == 1);
        }
        {
            var t = new DiplomacyTable();
            t.ProposeAlliance("Agus", "Bea");
            t.ProposeAlliance("bea", "agus");
            Check("Alliance_CaseInsensitive", t.Get("AGUS", "bEa", 0) == Relation.Ally);
        }
        {
            var t = Allied("a", "b", "a", "c", "a", "d", "a", "x");
            Check("Alliance_Cap4.count", t.AllyCount("a") == DiplomacyTable.MaxAllies);
            t.ProposeAlliance("e", "a");
            Check("Alliance_Cap4.reason", t.ProposeAlliance("a", "e") == "Diplomacy.MaxAllies");
            Check("Alliance_Cap4.neutral", t.Get("a", "e", 0) == Relation.Neutral);
            Check("Alliance_Cap4.noPending", t.Entries.TrueForAll(en => string.IsNullOrEmpty(en.PendingAllyFrom) || !(Has(en, "a") && Has(en, "e"))));
            Check("Alliance_Cap4.otherSideFull", t.ProposeAlliance("f", "a") == "Diplomacy.MaxAllies");
        }
        {
            var t = new DiplomacyTable();
            Check("Hostile_Declare", t.DeclareHostile("a", "b", 1000) == null);
            Check("Hostile_WarningPeriod.before", t.Get("a", "b", 1000 + DiplomacyTable.HostileWarningSeconds - 1) == Relation.Neutral);
            Check("Hostile_WarningPeriod.pending", t.IsHostilePending("b", "a", 1000 + DiplomacyTable.HostileWarningSeconds - 1));
            Check("Hostile_WarningPeriod.after", t.Get("b", "a", 1000 + DiplomacyTable.HostileWarningSeconds) == Relation.Hostile);
            Check("Hostile_WarningPeriod.notPendingAfter", !t.IsHostilePending("a", "b", 1000 + DiplomacyTable.HostileWarningSeconds));
        }
        {
            var t = new DiplomacyTable(0);
            Check("Hostile_NoWarning", t.DeclareHostile("a", "b", 5) == null && t.Get("a", "b", 5) == Relation.Hostile);
        }
        {
            var t = Allied("a", "b");
            t.DeclareHostile("a", "b", 0);
            Check("Hostile_BreaksAlliance", t.AllyCount("a") == 0 && t.AllyCount("b") == 0);
            Check("Hostile_CannotAllyWhileHostile", t.ProposeAlliance("a", "b") == "Diplomacy.HostileFirst");
        }
        {
            var t = new DiplomacyTable(0);
            t.DeclareHostile("a", "b", 0);
            t.ProposePeace("a", "b");
            Check("Peace_NeedsBoth.one", t.Get("a", "b", 10) == Relation.Hostile);
            t.ProposePeace("b", "a");
            Check("Peace_NeedsBoth.both", t.Get("a", "b", 10) == Relation.Neutral);
            Check("Peace_NotHostile", t.ProposePeace("a", "b") == "Diplomacy.NotHostile");
        }
        {
            var t = Allied("a", "b");
            Check("Break_Alliance", t.BreakAlliance("b", "a") == null && t.Get("a", "b", 0) == Relation.Neutral);
            Check("Break_NotAllied", t.BreakAlliance("a", "b") == "Diplomacy.NotAllied");
        }
        {
            var t = new DiplomacyTable();
            Check("SelfRelation_Rejected", t.ProposeAlliance("a", "A") == "Diplomacy.Self" && t.DeclareHostile("a", "a", 0) == "Diplomacy.Self");
        }
        {
            var t = Allied("a|1", "b");
            t.DeclareHostile("c", "d", 1234);
            t.ProposeAlliance("e", "f");
            t.DeclareHostile("g", "h", 0);
            t.ProposePeace("h", "g");
            string path = Path.Combine(Path.GetTempPath(), "rimcoop_diplo_test.txt");
            t.Save(path);
            var u = new DiplomacyTable();
            u.Load(path);
            File.Delete(path);
            Check("SaveLoad.ally", u.Get("a|1", "b", 0) == Relation.Ally);
            Check("SaveLoad.hostileSince", u.IsHostilePending("c", "d", 1234) && u.Get("c", "d", 1234 + DiplomacyTable.HostileWarningSeconds) == Relation.Hostile);
            Check("SaveLoad.pendingAlly", u.ProposeAlliance("f", "e") == null && u.Get("e", "f", 0) == Relation.Ally);
            Check("SaveLoad.pendingPeace", u.ProposePeace("g", "h") == null && u.Get("g", "h", 0) == Relation.Neutral);
        }
        {
            var u = new DiplomacyTable();
            u.Load(Path.Combine(Path.GetTempPath(), "rimcoop_no_such_file.txt"));
            Check("Load_MissingFile", u.Entries.Count == 0);
        }

        {
            var t = new DiplomacyTable(0);
            t.ProposeAlliance("a", "b");
            Check("Decline_Alliance", t.Decline("b", "a") == null && t.ProposeAlliance("b", "a") == null && t.Get("a", "b", 0) == Relation.Neutral);
            Check("Decline_Nothing", t.Decline("c", "d") == "Diplomacy.NothingToDecline");
            Check("Decline_OwnProposal", t.Decline("b", "a") == "Diplomacy.NothingToDecline"); // b propuso: no puede "rechazar" la suya
            t.DeclareHostile("e", "f", 0);
            t.ProposePeace("e", "f");
            Check("Decline_Peace", t.Decline("f", "e") == null && t.Get("e", "f", 0) == Relation.Hostile && t.ProposePeace("f", "e") == null && t.Get("e", "f", 0) == Relation.Hostile);
        }
        {
            // Lado cliente: la tabla que manda el servidor reemplaza la anterior entera.
            var t = Allied("a", "b");
            t.Replace(new System.Collections.Generic.List<DiplomacyEntry>
            {
                new DiplomacyEntry { A = "a", B = "c", State = Relation.Ally },
                new DiplomacyEntry { A = "a", B = "d", State = Relation.Hostile, HostileSinceUnix = 10 },
                new DiplomacyEntry { A = "x", B = "x", State = Relation.Ally },
            });
            Check("Replace_FromServer.old", t.Get("a", "b", 0) == Relation.Neutral);
            Check("Replace_FromServer.new", t.Get("A", "c", 0) == Relation.Ally && t.AllyCount("a") == 1 && t.IsHostilePending("a", "d", 10));
            Check("Replace_FromServer.selfIgnored", t.AllyCount("x") == 0);
            t.Replace(null);
            Check("Replace_Null", t.Entries.Count == 0);
        }

        WireTests();
        ServerTests();

        Console.WriteLine(_failed == 0 ? "ALL OK" : _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }

    static Packet RoundTrip(Packet p)
    {
        using (var ms = new MemoryStream(NetIO.EncodeFrame(p))) return NetIO.ReadPacket(ms);
    }

    static void WireTests()
    {
        var msg = (DiplomacyMessagePayload)RoundTrip(Packet.Create(PacketType.DiplomacyMessage,
            new DiplomacyMessagePayload { FromPlayerId = 3, FromPlayerName = null, ToPlayerId = 7, Kind = "ally" })).Payload;
        Check("Wire_Message", msg.FromPlayerId == 3 && msg.FromPlayerName == "" && msg.ToPlayerId == 7 && msg.Kind == "ally");

        var state = new DiplomacyStatePayload { ServerNowUnix = 1234567890123L };
        state.Entries.Add(new DiplomacyEntry { A = "Agus", B = "Bea", State = Relation.Hostile, HostileSinceUnix = 99, PendingAllyFrom = null, PendingPeaceFrom = "Bea" });
        state.Entries.Add(new DiplomacyEntry { A = "c", B = "d", State = Relation.Ally });
        var back = (DiplomacyStatePayload)RoundTrip(Packet.Create(PacketType.DiplomacyState, state)).Payload;
        Check("Wire_State.now", back.ServerNowUnix == 1234567890123L && back.Entries.Count == 2);
        var e0 = back.Entries[0];
        Check("Wire_State.entry", e0.A == "Agus" && e0.B == "Bea" && e0.State == Relation.Hostile && e0.HostileSinceUnix == 99
            && e0.PendingAllyFrom == "" && e0.PendingPeaceFrom == "Bea" && back.Entries[1].State == Relation.Ally);
    }

    // Cliente mínimo contra el servidor real (sin RimWorld): handshake, y todo lo que llega queda en Inbox.
    class TestClient
    {
        public volatile int Id;
        private readonly System.Net.Sockets.TcpClient _tcp = new System.Net.Sockets.TcpClient();
        private System.Net.Sockets.NetworkStream _s;
        public readonly System.Collections.Concurrent.BlockingCollection<Packet> Inbox = new System.Collections.Concurrent.BlockingCollection<Packet>();

        public static TestClient Connect(int port, string name)
        {
            var c = new TestClient();
            c._tcp.Connect("127.0.0.1", port);
            c._s = c._tcp.GetStream();
            NetIO.SendPacket(c._s, Packet.Create(PacketType.Handshake, new HandshakePayload { PlayerName = name, ProtocolVersion = ProtocolInfo.Version }));
            new System.Threading.Thread(() =>
            {
                try
                {
                    Packet p;
                    while ((p = NetIO.ReadPacket(c._s)) != null)
                    {
                        if (p.Type == PacketType.WorldData) c.Id = p.GetPayload<WorldDataPayload>().AssignedPlayerId;
                        c.Inbox.Add(p);
                    }
                }
                catch { }
            }) { IsBackground = true }.Start();
            c.WaitFor(PacketType.WorldData, 3000);
            return c;
        }

        public void Send(PacketType t, object payload) => NetIO.SendPacket(_s, Packet.Create(t, payload));

        public Packet WaitFor(PacketType t, int ms, Func<Packet, bool> pred = null)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                if (Inbox.TryTake(out var p, 50) && p.Type == t && (pred == null || pred(p))) return p;
            }
            return null;
        }

        public void Close() { try { _tcp.Close(); } catch { } }
    }

    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static bool HasRelation(Packet p, string a, string b, Relation r) =>
        p != null && p.GetPayload<DiplomacyStatePayload>().Entries.Exists(e => e.State == r && Has(e, a) && Has(e, b));

    static void ServerTests()
    {
        CoopLog.OnMessage = _ => { };
        CoopLog.OnWarning = _ => { };
        string dir = Path.Combine(Path.GetTempPath(), "rimcoop_diplo_server_" + Guid.NewGuid().ToString("N"));
        int port = FreePort();
        var server = new CoopServer { DiplomacyWarningSeconds = 0 };
        server.Start(port, "seed", 0.3f, "Normal", "Normal", "Normal", dir);
        var ana = TestClient.Connect(port, "Ana");
        var beto = TestClient.Connect(port, "Beto");
        Check("Server_Connected", ana.Id > 0 && beto.Id > 0 && ana.Id != beto.Id);

        ana.Send(PacketType.AttackRequest, new TradeOrAttackPayload { ToPlayerId = beto.Id, Points = 300 });
        Check("Server_AttackNeedsHostile.blocked", beto.WaitFor(PacketType.AttackRequest, 800) == null);
        Check("Server_AttackNeedsHostile.told", ana.WaitFor(PacketType.Chat, 800, p => p.GetPayload<ChatPayload>().PlayerId == 0) != null);

        ana.Send(PacketType.DiplomacyMessage, new DiplomacyMessagePayload { ToPlayerId = beto.Id, Kind = "hostile" });
        Check("Server_Hostile.broadcast", HasRelation(beto.WaitFor(PacketType.DiplomacyState, 2000), "Ana", "Beto", Relation.Hostile)
            && HasRelation(ana.WaitFor(PacketType.DiplomacyState, 2000), "Ana", "Beto", Relation.Hostile));
        ana.Send(PacketType.AttackRequest, new TradeOrAttackPayload { ToPlayerId = beto.Id, Points = 300 });
        Check("Server_AttackNeedsHostile.allowed", beto.WaitFor(PacketType.AttackRequest, 2000) != null);

        ana.Send(PacketType.DiplomacyMessage, new DiplomacyMessagePayload { ToPlayerId = ana.Id, Kind = "ally" });
        Check("Server_Rejected.reason", ana.WaitFor(PacketType.Chat, 800, p => p.GetPayload<ChatPayload>().PlayerId == 0) != null);

        ana.Close();
        beto.Close();
        server.Stop();

        // Persistencia: otro servidor sobre la misma carpeta la sigue teniendo y se la manda al que entra.
        port = FreePort();
        var again = new CoopServer();
        again.Start(port, "seed", 0.3f, "Normal", "Normal", "Normal", dir);
        var beto2 = TestClient.Connect(port, "beto");
        Check("Server_Persisted", HasRelation(beto2.WaitFor(PacketType.DiplomacyState, 2000), "Ana", "Beto", Relation.Hostile));
        beto2.Close();
        again.Stop();
        try { Directory.Delete(dir, true); } catch { }
    }

    static bool Has(DiplomacyEntry e, string n) => string.Equals(e.A, n, StringComparison.OrdinalIgnoreCase) || string.Equals(e.B, n, StringComparison.OrdinalIgnoreCase);
}
