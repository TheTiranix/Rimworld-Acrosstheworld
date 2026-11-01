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

        WireTests();

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

    static bool Has(DiplomacyEntry e, string n) => string.Equals(e.A, n, StringComparison.OrdinalIgnoreCase) || string.Equals(e.B, n, StringComparison.OrdinalIgnoreCase);
}
