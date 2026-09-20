using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Sockets;

namespace RimCoopMod.Networking
{
    /// <summary>
    /// Framing: [4 bytes longitud][cuerpo binario: 1 byte tipo + campos].
    /// Serialización manual con BinaryWriter/BinaryReader, sin dependencias externas.
    /// </summary>
    public static class NetIO
    {
        public static void SendPacket(NetworkStream stream, Packet packet)
        {
            byte[] body;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)packet.Type);
                WritePayload(bw, packet);
                body = ms.ToArray();
            }

            byte[] lenPrefix = BitConverter.GetBytes(body.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(lenPrefix);

            lock (stream)
            {
                stream.Write(lenPrefix, 0, 4);
                stream.Write(body, 0, body.Length);
                stream.Flush();
            }
        }

        public static Packet ReadPacket(NetworkStream stream)
        {
            byte[] lenBuf = ReadExact(stream, 4);
            if (lenBuf == null) return null;
            if (BitConverter.IsLittleEndian) Array.Reverse(lenBuf);
            int len = BitConverter.ToInt32(lenBuf, 0);

            if (len <= 0 || len > 16 * 1024 * 1024)
                throw new IOException("Tamaño de paquete inválido: " + len);

            byte[] body = ReadExact(stream, len);
            if (body == null) return null;

            using (var ms = new MemoryStream(body))
            using (var br = new BinaryReader(ms))
            {
                var type = (PacketType)br.ReadByte();
                object payload = ReadPayload(br, type);
                return new Packet { Type = type, Payload = payload };
            }
        }

        private static void WritePayload(BinaryWriter bw, Packet packet)
        {
            switch (packet.Type)
            {
                case PacketType.Handshake:
                    {
                        var p = (HandshakePayload)packet.Payload;
                        bw.Write(p.PlayerName ?? "");
                        break;
                    }
                case PacketType.WorldData:
                    {
                        var p = (WorldDataPayload)packet.Payload;
                        bw.Write(p.Seed ?? "");
                        bw.Write(p.PlanetCoverage);
                        bw.Write(p.OverallRainfall ?? "");
                        bw.Write(p.OverallTemperature ?? "");
                        bw.Write(p.OverallPopulation ?? "");
                        bw.Write(p.AssignedPlayerId);
                        bw.Write(p.ExistingPlayers.Count);
                        foreach (var info in p.ExistingPlayers) WritePlayerBaseInfo(bw, info);
                        break;
                    }
                case PacketType.PlayerJoined:
                case PacketType.PlayerLeft:
                    {
                        WritePlayerBaseInfo(bw, (PlayerBaseInfo)packet.Payload);
                        break;
                    }
                case PacketType.PlayerUpdate:
                    {
                        var p = (PlayerUpdatePayload)packet.Payload;
                        bw.Write(p.PlayerId);
                        bw.Write(p.PlayerName ?? "");
                        bw.Write(p.Tile);
                        bw.Write(p.ColonistCount);
                        bw.Write(p.Wealth);
                        break;
                    }
                case PacketType.TradeRequest:
                case PacketType.AttackRequest:
                    {
                        var p = (TradeOrAttackPayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.FromPlayerName ?? "");
                        bw.Write(p.Points);
                        break;
                    }
                case PacketType.Chat:
                    {
                        var p = (ChatPayload)packet.Payload;
                        bw.Write(p.PlayerId);
                        bw.Write(p.PlayerName ?? "");
                        bw.Write(p.Message ?? "");
                        break;
                    }

                case PacketType.WatchRequest:
                case PacketType.UnwatchRequest:
                case PacketType.PlayersRequest:
                    {
                        var p = (WatchRequestPayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.ToPlayerId);
                        break;
                    }

                case PacketType.MapSnapshot:
                    {
                        var p = (MapSnapshotPayload)packet.Payload;
                        bw.Write(p.HostPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.MapWidth);
                        bw.Write(p.MapHeight);
                        bw.Write(p.WeatherDefName ?? "");
                        bw.Write(p.SkyGlow);
                        bw.Write(p.Pawns.Count);
                        foreach (var pawn in p.Pawns)
                        {
                            bw.Write(pawn.PawnId);
                            bw.Write(pawn.Label ?? "");
                            bw.Write(pawn.OwnerPlayerId);
                            bw.Write(pawn.X);
                            bw.Write(pawn.Z);
                            bw.Write(pawn.Rot);
                            bw.Write(pawn.Moving);
                            bw.Write(pawn.JobLabel ?? "");
                            bw.Write(pawn.Downed);
                            bw.Write(pawn.Dead);
                            bw.Write(pawn.Hostile);
                            bw.Write(pawn.Animal);
                            bw.Write(pawn.HasMedium);
                            if (pawn.HasMedium)
                            {
                                bw.Write(pawn.EquippedWeaponDefName ?? "");
                                bw.Write(pawn.EquippedWeaponStuffDefName ?? "");
                                bw.Write(pawn.InventoryCsv ?? "");
                                bw.Write(pawn.CarriedCsv ?? "");
                                bw.Write(pawn.ApparelCsv ?? "");
                                bw.Write(pawn.EquippedWeaponQuality);
                                bw.Write(pawn.EquippedWeaponHitPoints);
                                bw.Write(pawn.NeedsCsv ?? "");
                            }
                            bw.Write(pawn.CurJobDefName ?? "");
                            bw.Write(pawn.CurJobTargetAThingId);
                            bw.Write(pawn.CurJobTargetAX);
                            bw.Write(pawn.CurJobTargetAZ);
                            bw.Write(pawn.CurJobHasTargetB);
                            bw.Write(pawn.CurJobTargetBThingId);
                            bw.Write(pawn.CurJobTargetBX);
                            bw.Write(pawn.CurJobTargetBZ);
                            bw.Write(pawn.CurJobCount);
                            bw.Write(pawn.HasSlowData);
                            if (pawn.HasSlowData)
                            {
                                bw.Write(pawn.HediffsCsv ?? "");
                                bw.Write(pawn.MemoriesCsv ?? "");
                                bw.Write(pawn.WorkPrioCsv ?? "");
                                bw.Write(pawn.TimetableCsv ?? "");
                                bw.Write(pawn.AreaLabel ?? "");
                                bw.Write(pawn.SkillsCsv ?? "");
                                bw.Write(pawn.TraitsCsv ?? "");
                                bw.Write(pawn.TrainingCsv ?? "");
                                bw.Write(pawn.MasterPawnId);
                                bw.Write(pawn.BondsCsv ?? "");
                                bw.Write(pawn.GuestStatus ?? "");
                                bw.Write(pawn.GuestMode ?? "");
                                bw.Write(pawn.GuestResistance);
                                bw.Write(pawn.GuestWill);
                                bw.Write(pawn.IsPlayerFaction);
                            }
                        }
                        bw.Write(p.Interactions.Count);
                        foreach (var ie in p.Interactions)
                        {
                            bw.Write(ie.InitiatorId);
                            bw.Write(ie.RecipientId);
                            bw.Write(ie.DefName ?? "");
                        }
                        break;
                    }

                case PacketType.PawnOrder:
                    {
                        var p = (PawnOrderPayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.PawnId);
                        bw.Write(p.JobDefName ?? "");
                        bw.Write(p.TargetAThingId);
                        bw.Write(p.TargetAX);
                        bw.Write(p.TargetAZ);
                        bw.Write(p.HasTargetB);
                        bw.Write(p.TargetBThingId);
                        bw.Write(p.TargetBX);
                        bw.Write(p.TargetBZ);
                        bw.Write(p.Count);
                        break;
                    }

                case PacketType.JoinRequest:
                    {
                        var p = (JoinRequestPayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.SerializedPawns.Count);
                        foreach (var xml in p.SerializedPawns) bw.Write(xml ?? "");
                        break;
                    }

                case PacketType.JoinResult:
                    {
                        var p = (JoinResultPayload)packet.Payload;
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.Success);
                        bw.Write(p.Message ?? "");
                        break;
                    }

                case PacketType.BaseSnapshotRequest:
                    {
                        var p = (BaseSnapshotRequestPayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.ToPlayerId);
                        break;
                    }

                case PacketType.BaseSnapshot:
                    {
                        var p = (BaseSnapshotPayload)packet.Payload;
                        bw.Write(p.HostPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.IsDelta);
                        bw.Write(p.HasLayers);
                        bw.Write(p.Things.Count);
                        foreach (var t in p.Things)
                        {
                            bw.Write(t.ThingId);
                            bw.Write(t.DefName ?? "");
                            bw.Write(t.StuffDefName ?? "");
                            bw.Write(t.X);
                            bw.Write(t.Z);
                            bw.Write(t.Rotation);
                            bw.Write(t.StackCount);
                            bw.Write(t.HitPoints);
                            bw.Write(t.StateStr ?? "");
                        }
                        bw.Write(p.RemovedThingIds.Count);
                        foreach (int removedId in p.RemovedThingIds) bw.Write(removedId);
                        if (p.HasLayers)
                        {
                        bw.Write(p.Zones.Count);
                        foreach (var z in p.Zones)
                        {
                            bw.Write(z.Kind ?? "");
                            bw.Write(z.Label ?? "");
                            bw.Write(z.PlantDefName ?? "");
                            bw.Write(z.CellsCsv ?? "");
                            bw.Write(z.ZoneId);
                            bw.Write(z.SettingsStr ?? "");
                        }
                        bw.Write(p.Areas.Count);
                        foreach (var a in p.Areas)
                        {
                            bw.Write(a.Kind ?? "");
                            bw.Write(a.Label ?? "");
                            bw.Write(a.CellsCsv ?? "");
                        }
                        bw.Write(p.Stores.Count);
                        foreach (var st in p.Stores)
                        {
                            bw.Write(st.X);
                            bw.Write(st.Z);
                            bw.Write(st.SettingsStr ?? "");
                        }
                        WriteGridList(bw, p.Roofs);
                        WriteGridList(bw, p.Terrains);
                        WriteGridList(bw, p.Snow);
                        bw.Write(p.HasPlants);
                        WriteGridList(bw, p.Plants);
                        }
                        break;
                    }

                case PacketType.PawnAppearanceRequest:
                    {
                        var p = (PawnAppearanceRequestPayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.PawnId);
                        break;
                    }

                case PacketType.PawnAppearance:
                    {
                        var p = (PawnAppearancePayload)packet.Payload;
                        bw.Write(p.HostPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.PawnId);
                        bw.Write(p.SerializedPawn ?? "");
                        break;
                    }

                case PacketType.PauseVoteRequest:
                    {
                        var p = (PauseVoteRequestPayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.FromPlayerName ?? "");
                        bw.Write(p.ProposePause);
                        break;
                    }

                case PacketType.PauseVoteResponse:
                    {
                        var p = (PauseVoteResponsePayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.Accept);
                        break;
                    }

                case PacketType.PauseVoteResult:
                    {
                        var p = (PauseVoteResultPayload)packet.Payload;
                        bw.Write(p.Approved);
                        bw.Write(p.ProposePause);
                        break;
                    }

                case PacketType.BuildRequest:
                    {
                        var p = (BuildRequestPayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.DefName ?? "");
                        bw.Write(p.StuffDefName ?? "");
                        bw.Write(p.X);
                        bw.Write(p.Z);
                        bw.Write(p.Rotation);
                        break;
                    }

                case PacketType.EventNotice:
                    {
                        var p = (EventNoticePayload)packet.Payload;
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.LetterDefName ?? "");
                        bw.Write(p.Label ?? "");
                        bw.Write(p.Text ?? "");
                        break;
                    }

                case PacketType.ResearchSync:
                    {
                        var p = (ResearchSyncPayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.FromPlayerName ?? "");
                        bw.Write(p.Kind ?? "");
                        bw.Write(p.Data ?? "");
                        break;
                    }

                case PacketType.WorldEvent:
                    {
                        var p = (WorldEventPayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.FromPlayerName ?? "");
                        bw.Write(p.DefName ?? "");
                        bw.Write(p.Duration);
                        break;
                    }

                case PacketType.TradeMessage:
                    {
                        var p = (TradeMessagePayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.FromPlayerName ?? "");
                        bw.Write(p.Kind ?? "");
                        bw.Write(p.OfferId);
                        bw.Write(p.Data ?? "");
                        break;
                    }

                case PacketType.SpeedChange:
                    {
                        var p = (SpeedChangePayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.Speed);
                        break;
                    }

                case PacketType.PawnSettingRequest:
                    {
                        var p = (PawnSettingPayload)packet.Payload;
                        bw.Write(p.FromPlayerId);
                        bw.Write(p.ToPlayerId);
                        bw.Write(p.PawnId);
                        bw.Write(p.Kind ?? "");
                        bw.Write(p.Key ?? "");
                        bw.Write(p.Value ?? "");
                        break;
                    }
            }
        }

        private static void WriteGridList(BinaryWriter bw, List<RoofSnapshot> list)
        {
            bw.Write(list.Count);
            foreach (var r in list)
            {
                bw.Write(r.DefName ?? "");
                bw.Write(r.RunsCsv ?? "");
            }
        }

        private static void ReadGridList(BinaryReader br, List<RoofSnapshot> list)
        {
            int count = br.ReadInt32();
            for (int i = 0; i < count; i++) list.Add(new RoofSnapshot { DefName = br.ReadString(), RunsCsv = br.ReadString() });
        }

        // --- Este método faltaba en la versión anterior, causaba el error CS0103 ---
        private static void WritePlayerBaseInfo(BinaryWriter bw, PlayerBaseInfo info)
        {
            bw.Write(info.PlayerId);
            bw.Write(info.PlayerName ?? "");
            bw.Write(info.Tile);
            bw.Write(info.ColonistCount);
            bw.Write(info.Wealth);
        }

        private static object ReadPayload(BinaryReader br, PacketType type)
        {
            switch (type)
            {
                case PacketType.Handshake:
                    return new HandshakePayload { PlayerName = br.ReadString() };

                case PacketType.WorldData:
                    {
                        var p = new WorldDataPayload
                        {
                            Seed = br.ReadString(),
                            PlanetCoverage = br.ReadSingle(),
                            OverallRainfall = br.ReadString(),
                            OverallTemperature = br.ReadString(),
                            OverallPopulation = br.ReadString(),
                            AssignedPlayerId = br.ReadInt32()
                        };
                        int count = br.ReadInt32();
                        for (int i = 0; i < count; i++) p.ExistingPlayers.Add(ReadPlayerBaseInfo(br));
                        return p;
                    }

                case PacketType.PlayerJoined:
                case PacketType.PlayerLeft:
                    return ReadPlayerBaseInfo(br);

                case PacketType.PlayerUpdate:
                    return new PlayerUpdatePayload
                    {
                        PlayerId = br.ReadInt32(),
                        PlayerName = br.ReadString(),
                        Tile = br.ReadInt32(),
                        ColonistCount = br.ReadInt32(),
                        Wealth = br.ReadInt32()
                    };

                case PacketType.TradeRequest:
                case PacketType.AttackRequest:
                    return new TradeOrAttackPayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32(),
                        FromPlayerName = br.ReadString(),
                        Points = br.ReadInt32()
                    };

                case PacketType.Chat:
                    return new ChatPayload
                    {
                        PlayerId = br.ReadInt32(),
                        PlayerName = br.ReadString(),
                        Message = br.ReadString()
                    };

                case PacketType.WatchRequest:
                case PacketType.UnwatchRequest:
                case PacketType.PlayersRequest:
                    return new WatchRequestPayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32()
                    };

                case PacketType.MapSnapshot:
                    {
                        var p = new MapSnapshotPayload
                        {
                            HostPlayerId = br.ReadInt32(),
                            ToPlayerId = br.ReadInt32(),
                            MapWidth = br.ReadInt32(),
                            MapHeight = br.ReadInt32(),
                            WeatherDefName = br.ReadString(),
                            SkyGlow = br.ReadSingle()
                        };
                        int count = br.ReadInt32();
                        for (int i = 0; i < count; i++)
                        {
                            var ps = new PawnSnapshot
                            {
                                PawnId = br.ReadInt32(),
                                Label = br.ReadString(),
                                OwnerPlayerId = br.ReadInt32(),
                                X = br.ReadInt32(),
                                Z = br.ReadInt32(),
                                Rot = br.ReadInt32(),
                                Moving = br.ReadBoolean(),
                                JobLabel = br.ReadString(),
                                Downed = br.ReadBoolean(),
                                Dead = br.ReadBoolean(),
                                Hostile = br.ReadBoolean(),
                                Animal = br.ReadBoolean()
                            };
                            ps.HasMedium = br.ReadBoolean();
                            if (ps.HasMedium)
                            {
                                ps.EquippedWeaponDefName = br.ReadString();
                                ps.EquippedWeaponStuffDefName = br.ReadString();
                                ps.InventoryCsv = br.ReadString();
                                ps.CarriedCsv = br.ReadString();
                                ps.ApparelCsv = br.ReadString();
                                ps.EquippedWeaponQuality = br.ReadInt32();
                                ps.EquippedWeaponHitPoints = br.ReadInt32();
                                ps.NeedsCsv = br.ReadString();
                            }
                            ps.CurJobDefName = br.ReadString();
                            ps.CurJobTargetAThingId = br.ReadInt32();
                            ps.CurJobTargetAX = br.ReadInt32();
                            ps.CurJobTargetAZ = br.ReadInt32();
                            ps.CurJobHasTargetB = br.ReadBoolean();
                            ps.CurJobTargetBThingId = br.ReadInt32();
                            ps.CurJobTargetBX = br.ReadInt32();
                            ps.CurJobTargetBZ = br.ReadInt32();
                            ps.CurJobCount = br.ReadInt32();
                            ps.HasSlowData = br.ReadBoolean();
                            if (ps.HasSlowData)
                            {
                                ps.HediffsCsv = br.ReadString();
                                ps.MemoriesCsv = br.ReadString();
                                ps.WorkPrioCsv = br.ReadString();
                                ps.TimetableCsv = br.ReadString();
                                ps.AreaLabel = br.ReadString();
                                ps.SkillsCsv = br.ReadString();
                                ps.TraitsCsv = br.ReadString();
                                ps.TrainingCsv = br.ReadString();
                                ps.MasterPawnId = br.ReadInt32();
                                ps.BondsCsv = br.ReadString();
                                ps.GuestStatus = br.ReadString();
                                ps.GuestMode = br.ReadString();
                                ps.GuestResistance = br.ReadSingle();
                                ps.GuestWill = br.ReadSingle();
                                ps.IsPlayerFaction = br.ReadBoolean();
                            }
                            p.Pawns.Add(ps);
                        }
                        int interCount = br.ReadInt32();
                        for (int i = 0; i < interCount; i++)
                        {
                            p.Interactions.Add(new InteractionEvent
                            {
                                InitiatorId = br.ReadInt32(),
                                RecipientId = br.ReadInt32(),
                                DefName = br.ReadString()
                            });
                        }
                        return p;
                    }

                case PacketType.PawnOrder:
                    return new PawnOrderPayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32(),
                        PawnId = br.ReadInt32(),
                        JobDefName = br.ReadString(),
                        TargetAThingId = br.ReadInt32(),
                        TargetAX = br.ReadInt32(),
                        TargetAZ = br.ReadInt32(),
                        HasTargetB = br.ReadBoolean(),
                        TargetBThingId = br.ReadInt32(),
                        TargetBX = br.ReadInt32(),
                        TargetBZ = br.ReadInt32(),
                        Count = br.ReadInt32()
                    };

                case PacketType.JoinRequest:
                    {
                        var p = new JoinRequestPayload
                        {
                            FromPlayerId = br.ReadInt32(),
                            ToPlayerId = br.ReadInt32()
                        };
                        int count = br.ReadInt32();
                        for (int i = 0; i < count; i++) p.SerializedPawns.Add(br.ReadString());
                        return p;
                    }

                case PacketType.JoinResult:
                    return new JoinResultPayload
                    {
                        ToPlayerId = br.ReadInt32(),
                        FromPlayerId = br.ReadInt32(),
                        Success = br.ReadBoolean(),
                        Message = br.ReadString()
                    };

                case PacketType.BaseSnapshotRequest:
                    return new BaseSnapshotRequestPayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32()
                    };

                case PacketType.BaseSnapshot:
                    {
                        var p = new BaseSnapshotPayload
                        {
                            HostPlayerId = br.ReadInt32(),
                            ToPlayerId = br.ReadInt32(),
                            IsDelta = br.ReadBoolean(),
                            HasLayers = br.ReadBoolean()
                        };
                        int count = br.ReadInt32();
                        for (int i = 0; i < count; i++)
                        {
                            p.Things.Add(new ThingSnapshot
                            {
                                ThingId = br.ReadInt32(),
                                DefName = br.ReadString(),
                                StuffDefName = br.ReadString(),
                                X = br.ReadInt32(),
                                Z = br.ReadInt32(),
                                Rotation = br.ReadInt32(),
                                StackCount = br.ReadInt32(),
                                HitPoints = br.ReadInt32(),
                                StateStr = br.ReadString()
                            });
                        }
                        int removedCount = br.ReadInt32();
                        for (int i = 0; i < removedCount; i++) p.RemovedThingIds.Add(br.ReadInt32());
                        if (p.HasLayers)
                        {
                        int zoneCount = br.ReadInt32();
                        for (int i = 0; i < zoneCount; i++)
                        {
                            p.Zones.Add(new ZoneSnapshot
                            {
                                Kind = br.ReadString(),
                                Label = br.ReadString(),
                                PlantDefName = br.ReadString(),
                                CellsCsv = br.ReadString(),
                                ZoneId = br.ReadInt32(),
                                SettingsStr = br.ReadString()
                            });
                        }
                        int areaCount = br.ReadInt32();
                        for (int i = 0; i < areaCount; i++)
                        {
                            p.Areas.Add(new AreaSnapshot
                            {
                                Kind = br.ReadString(),
                                Label = br.ReadString(),
                                CellsCsv = br.ReadString()
                            });
                        }
                        int storeCount = br.ReadInt32();
                        for (int i = 0; i < storeCount; i++)
                        {
                            p.Stores.Add(new StoreSnapshot
                            {
                                X = br.ReadInt32(),
                                Z = br.ReadInt32(),
                                SettingsStr = br.ReadString()
                            });
                        }
                        ReadGridList(br, p.Roofs);
                        ReadGridList(br, p.Terrains);
                        ReadGridList(br, p.Snow);
                        p.HasPlants = br.ReadBoolean();
                        ReadGridList(br, p.Plants);
                        }
                        return p;
                    }

                case PacketType.PawnAppearanceRequest:
                    return new PawnAppearanceRequestPayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32(),
                        PawnId = br.ReadInt32()
                    };

                case PacketType.PawnAppearance:
                    return new PawnAppearancePayload
                    {
                        HostPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32(),
                        PawnId = br.ReadInt32(),
                        SerializedPawn = br.ReadString()
                    };

                case PacketType.PauseVoteRequest:
                    return new PauseVoteRequestPayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        FromPlayerName = br.ReadString(),
                        ProposePause = br.ReadBoolean()
                    };

                case PacketType.PauseVoteResponse:
                    return new PauseVoteResponsePayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32(),
                        Accept = br.ReadBoolean()
                    };

                case PacketType.PauseVoteResult:
                    return new PauseVoteResultPayload
                    {
                        Approved = br.ReadBoolean(),
                        ProposePause = br.ReadBoolean()
                    };

                case PacketType.BuildRequest:
                    return new BuildRequestPayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32(),
                        DefName = br.ReadString(),
                        StuffDefName = br.ReadString(),
                        X = br.ReadInt32(),
                        Z = br.ReadInt32(),
                        Rotation = br.ReadInt32()
                    };

                case PacketType.EventNotice:
                    return new EventNoticePayload
                    {
                        ToPlayerId = br.ReadInt32(),
                        FromPlayerId = br.ReadInt32(),
                        LetterDefName = br.ReadString(),
                        Label = br.ReadString(),
                        Text = br.ReadString()
                    };

                case PacketType.ResearchSync:
                    return new ResearchSyncPayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32(),
                        FromPlayerName = br.ReadString(),
                        Kind = br.ReadString(),
                        Data = br.ReadString()
                    };

                case PacketType.WorldEvent:
                    return new WorldEventPayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        FromPlayerName = br.ReadString(),
                        DefName = br.ReadString(),
                        Duration = br.ReadInt32()
                    };

                case PacketType.TradeMessage:
                    return new TradeMessagePayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32(),
                        FromPlayerName = br.ReadString(),
                        Kind = br.ReadString(),
                        OfferId = br.ReadInt32(),
                        Data = br.ReadString()
                    };

                case PacketType.SpeedChange:
                    return new SpeedChangePayload { FromPlayerId = br.ReadInt32(), Speed = br.ReadInt32() };

                case PacketType.PawnSettingRequest:
                    return new PawnSettingPayload
                    {
                        FromPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32(),
                        PawnId = br.ReadInt32(),
                        Kind = br.ReadString(),
                        Key = br.ReadString(),
                        Value = br.ReadString()
                    };

                default:
                    return null;
            }
        }

        private static PlayerBaseInfo ReadPlayerBaseInfo(BinaryReader br)
        {
            return new PlayerBaseInfo
            {
                PlayerId = br.ReadInt32(),
                PlayerName = br.ReadString(),
                Tile = br.ReadInt32(),
                ColonistCount = br.ReadInt32(),
                Wealth = br.ReadInt32()
            };
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0) return null;
                offset += read;
            }
            return buffer;
        }
    }
}