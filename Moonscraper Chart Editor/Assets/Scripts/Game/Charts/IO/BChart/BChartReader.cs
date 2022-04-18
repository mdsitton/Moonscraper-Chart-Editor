using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BinaryEx;
using MoonscraperChartEditor.Song;
using MoonscraperChartEditor.Song.IO;
using UnityEngine;
using static MoonscraperChartEditor.Song.Song;

namespace MoonscraperChartEditor.Song.IO
{

    public static class BChartReader
    {
        public static string ReadTextEventData(Span<byte> data)
        {
            return Encoding.UTF8.GetString(data);
        }

        public static uint ReadPhraseLength(Span<byte> data)
        {
            return data.ReadUInt32LE(0);
        }

        public static Note ReadNoteData(Span<byte> data, uint tick)
        {
            int pos = 0;
            byte noteValue = data.ReadByte(ref pos);
            uint tickLength = data.ReadUInt32LE(ref pos);
            byte modifierCount = data.ReadByte(ref pos);

            if (modifierCount > pos + data.Length)
            {
                modifierCount = (byte)(data.Length - pos);
            }

            Note note = new Note(tick, noteValue, tickLength);

            for (int i = 0; i < modifierCount; ++i)
            {
                byte modifier = data.ReadByte(ref pos);

                switch (modifier)
                {
                    case BChartConsts.MODIFIER_FORCED:
                        note.forced = true;
                        break;
                    case BChartConsts.MODIFIER_TAP:
                        note.flags = Note.Flags.Tap;
                        break;
                    case BChartConsts.MODIFIER_DRUMS_ACCENT:
                        note.flags |= Note.Flags.ProDrums_Accent;
                        break;
                    case BChartConsts.MODIFIER_DRUMS_GHOST:
                        note.flags |= Note.Flags.ProDrums_Ghost;
                        break;
                }
            }
            return note;
        }

        public static BPM ReadTempoData(Span<byte> data, uint tickPos)
        {
            uint tempo = data.ReadUInt32LE(0);
            uint bpm = (uint)(60000000000.0 / tempo);
            return new BPM(tickPos, bpm);
        }

        public static TimeSignature ReadTSData(Span<byte> data, uint tickPos)
        {
            int pos = 0;
            var num = data.ReadByte(ref pos);
            var den = data.ReadByte(ref pos);

            return new TimeSignature(tickPos, num, den);
        }


        public static (uint tickPos, byte eventType) ReadEventBytes(Span<byte> data, ref int pos, out Span<byte> eventSpanOut)
        {
            uint tickPos = data.ReadUInt32LE(ref pos);
            byte eventType = data.ReadByte(ref pos);
            byte eventLength = data.ReadByte(ref pos);

            eventSpanOut = data.Slice(pos, eventLength);
            pos += eventLength;
            return (tickPos, eventType);
        }

        public static (uint version, uint instrumentCount) ReadHeader(Span<byte> data, Song song)
        {
            int pos = 0;

            uint version = data.ReadUInt16LE(ref pos);
            uint resolution = data.ReadUInt16LE(ref pos);
            uint instrumentCount = data.ReadUInt16LE(ref pos);

            song.resolution = resolution;

            return (version, instrumentCount);
        }

        public static void ReadTempoMap(Span<byte> data, Song song)
        {
            int pos = 0;
            uint eventCount = data.ReadUInt32LE(ref pos);
            for (int i = 0; i < eventCount; ++i)
            {

                (uint tickPos, byte eventType) = ReadEventBytes(data, ref pos, out Span<byte> dataSpan);

                switch (eventType)
                {
                    case BChartConsts.EVENT_TEMPO:
                        song.Add(ReadTempoData(dataSpan, tickPos), false);
                        break;
                    case BChartConsts.EVENT_TIME_SIG:
                        song.Add(ReadTSData(dataSpan, tickPos), false);
                        break;
                }
            }
            song.UpdateCache();
        }

        public static void ReadGlobalEvents(Span<byte> data, Song song)
        {
            int pos = 0;
            uint eventCount = data.ReadUInt32LE(ref pos);
            for (int i = 0; i < eventCount; ++i)
            {

                (uint tickPos, byte eventType) = ReadEventBytes(data, ref pos, out Span<byte> dataSpan);

                switch (eventType)
                {
                    case BChartConsts.EVENT_SECTION:
                        {

                            string txt = ReadTextEventData(dataSpan);
                            song.Add(new Section(txt, tickPos), false);
                            break;
                        }
                    case BChartConsts.EVENT_TEXT:
                        {
                            string txt = ReadTextEventData(dataSpan);
                            song.Add(new Event(txt, tickPos), false);
                            break;
                        }
                }
            }
            song.UpdateCache();
        }

        public static void ReadDifficulty(Span<byte> data, Song song, Instrument inst)
        {
            int pos = 0;
            int eventCount = data.ReadInt32LE(ref pos);
            Difficulty diff = (Difficulty)data.ReadByte(ref pos);
            List<ChartEvent> soloEndEvents = new List<ChartEvent>();
            Console.WriteLine($"{inst} {diff}");
            var chart = song.GetChart(inst, diff);
            for (int i = 0; i < eventCount; ++i)
            {
                (uint tickPos, byte eventType) = ReadEventBytes(data, ref pos, out Span<byte> dataSpan);


                for (int j = 0; j < soloEndEvents.Count; ++j)
                {
                    var end = soloEndEvents[j];
                    if (tickPos > end.tick)
                    {
                        chart.Add(end, false);
                        soloEndEvents.RemoveAt(j--);
                    }
                }

                switch (eventType)
                {
                    case BChartConsts.EVENT_NOTE:
                        Note note = ReadNoteData(dataSpan, tickPos);
                        chart.Add(note, false);
                        break;
                    case BChartConsts.EVENT_PHRASE:
                        {
                            var type = dataSpan.ReadByte(0);

                            if (type == BChartConsts.PHRASE_STARPOWER)
                            {

                                uint length = ReadPhraseLength(dataSpan);
                                chart.Add(new Starpower(tickPos, length), false);
                            }
                            else if (type == BChartConsts.PHRASE_SOLO)
                            {
                                uint length = ReadPhraseLength(dataSpan);
                                var start = new ChartEvent(tickPos, MidIOHelper.SoloEventText);
                                var end = new ChartEvent(tickPos + length, MidIOHelper.SoloEndEventText);
                                soloEndEvents.Add(end);
                                chart.Add(start, false);
                                // chart.Add(end, false);
                            }
                            break;
                        }
                    case BChartConsts.EVENT_TEXT:
                        string txt = ReadTextEventData(dataSpan);
                        chart.Add(new ChartEvent(tickPos, txt), false);
                        break;
                    default:
                        // Skip any unknown event types
                        break;
                }
            }
            chart.UpdateCache();
        }

        public static (Instrument inst, byte count) ReadInstrument(Span<byte> data)
        {
            int pos = 0;
            return ((Instrument)data.ReadUInt32LE(ref pos), data.ReadByte(ref pos));
        }

        public static Song ReadBChart(string path)
        {
            Song song = new Song();
            string directory = Path.GetDirectoryName(path);

            MsceIOHelper.DiscoverAudio(directory, song);

            byte[] fileData = File.ReadAllBytes(path);
            Span<byte> data = fileData;
            int pos = 0;
            Instrument currentInstrument = Instrument.Unrecognised;
            byte diffCount = 0;

            uint version;
            uint instrumentCount;

            while (pos < data.Length)
            {
                // Console.WriteLine(pos);
                var chunkID = data.ReadUInt32LE(ref pos);
                var chunkLength = data.ReadInt32LE(ref pos);
                var chunkData = data.Slice(pos, chunkLength);
                pos += chunkLength;

                if (chunkID == BChartConsts.HeaderChunkName)
                {
                    (version, instrumentCount) = ReadHeader(chunkData, song);
                }
                else if (chunkID == BChartConsts.TempoChunkName)
                {
                    ReadTempoMap(chunkData, song);
                }
                else if (chunkID == BChartConsts.GlobalEventsChunkName)
                {
                    ReadGlobalEvents(chunkData, song);
                }
                else if (chunkID == BChartConsts.InstrumentChunkName)
                {
                    (currentInstrument, diffCount) = ReadInstrument(chunkData);
                }
                else if (chunkID == BChartConsts.DifficultyChunkName)
                {
                    ReadDifficulty(chunkData, song, currentInstrument);
                }

            }

            return song;
        }
    }
}