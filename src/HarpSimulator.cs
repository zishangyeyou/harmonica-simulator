using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;


namespace DeltaHarp
{
    public class MidiNote
    {
        public int StartMs;
        public int EndMs;
        public int SoundMs;
        public int Midi;
        public int Vel;
        public int Ch;
        public int Track;
        public Finger Fing;
        public bool Folded;
        public bool Unplayable;
    }

    public class Finger
    {
        public string Key;
        public int Hole;
        public bool Left;
        public bool Middle;
        public bool Right;
        public int Midi;
        public string Name;
        public string Layer;
    }

    public class MacroStep
    {
        public int AbsMs;
        public int DelayMs;
        public string Action;
        public string Code;
        public string Note;
        public int Hole;
        public string Layer;
    }

    /// <summary>
    /// 输入时序预算，全部为**物理毫秒**，与播放速度无关。
    /// 某些目标程序的按键是按帧采样的（30fps ≈ 33ms/帧）。修饰键（鼠标左/中/右）和音键
    /// 如果落在同一帧里，目标程序只会看到最后的状态，于是就漏音、变调。所以这些
    /// 间隔必须按物理帧给足，不能被播放速度除小。对应原版 HarpAutoPlayer 的档位表。
    /// </summary>
    public class InputProfile
    {
        public string Name;
        /// <summary>采样帧长估计：16.7 = 60fps，33.3 = 30fps。</summary>
        public int FrameMs;
        /// <summary>修饰键必须比音键早这么多毫秒。</summary>
        public int ModLeadMs;
        /// <summary>同一根音键两次按下之间的最小间隔（要跨过"抬起"那一帧）。</summary>
        public int RetriggerMs;
        /// <summary>音键最短按住时长（避免按下与抬起折进同一帧）。</summary>
        public int MinHoldMs;
        /// <summary>前音抬起 → 后音按下之间的最小安全间隔。</summary>
        public int ReleaseGapMs;

        public static InputProfile Safe()
        {
            InputProfile p = new InputProfile();
            p.Name = "稳健(30fps/卡顿)";
            p.FrameMs = 33; p.ModLeadMs = 70; p.RetriggerMs = 80;
            p.MinHoldMs = 80; p.ReleaseGapMs = 70;
            return p;
        }

        public static InputProfile Standard()
        {
            InputProfile p = new InputProfile();
            p.Name = "标准(60fps推荐)";
            p.FrameMs = 17; p.ModLeadMs = 40; p.RetriggerMs = 45;
            p.MinHoldMs = 45; p.ReleaseGapMs = 40;
            return p;
        }

        public static InputProfile Aggressive()
        {
            InputProfile p = new InputProfile();
            p.Name = "极限(高帧率)";
            p.FrameMs = 8; p.ModLeadMs = 20; p.RetriggerMs = 22;
            p.MinHoldMs = 22; p.ReleaseGapMs = 18;
            return p;
        }

        /// <summary>0 稳健 / 1 标准 / 2 极限</summary>
        public static InputProfile FromIndex(int i)
        {
            if (i == 0) return Safe();
            if (i == 2) return Aggressive();
            return Standard();
        }

        public static string[] Names()
        {
            return new string[] { "稳健（30fps / 卡顿）", "标准（60fps 推荐）", "极限（高帧率）" };
        }
    }

    public class ChannelInfo
    {
        public int Ch;
        public int Count;
        public int InRange;
        public int MinMidi;
        public int MaxMidi;
        public int StartMs;
        public int Overlaps;
        public int Score;
        public int Program;
        public int Track;
        public string Name;
        public string Role;
        /// <summary>MIDI 通道 10（索引 9）是打击乐轨，不能当主旋律。</summary>
        public bool IsDrum;
    }

    public static class Util
    {
        public static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        public static readonly string[] KeyNames = { "Z", "X", "C", "V", "B", "N", "M", "," };
        public static readonly int[] Pc = { 0, 2, 4, 5, 7, 9, 11, 0 };
        public static readonly int[] Scan = { 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33 };

        public static string NoteName(int n)
        {
            if (n < 0 || n > 127) return "?";
            return Names[n % 12] + (n / 12 - 1).ToString();
        }

        public static int PitchOf(int hole, int octShift, bool sharp)
        {
            int baseOct = 4 + octShift;
            int midi;
            if (hole == 7) midi = (baseOct + 2) * 12;
            else midi = (baseOct + 1) * 12 + Pc[hole];
            if (sharp) midi += 1;
            return midi;
        }

        public static Finger Map(int midi)
        {
            Finger best = null;
            int bestCost = 999;
            for (int oct = -1; oct <= 1; oct++)
            {
                for (int s = 0; s <= 1; s++)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        if (PitchOf(i, oct, s == 1) != midi) continue;
                        int cost = Math.Abs(oct) * 2 + s;
                        if (cost < bestCost || (cost == bestCost && best != null && i < best.Hole))
                        {
                            bestCost = cost;
                            Finger f = new Finger();
                            f.Key = KeyNames[i];
                            f.Hole = i;
                            f.Left = oct < 0;
                            f.Right = oct > 0;
                            f.Middle = s == 1;
                            f.Midi = midi;
                            f.Name = NoteName(midi);
                            f.Layer = LayerText(f.Left, f.Middle, f.Right);
                            best = f;
                        }
                    }
                }
            }
            return best;
        }

        public static Finger MapFold(int midi, out bool folded)
        {
            folded = false;
            Finger f = Map(midi);
            if (f != null) return f;
            if (midi < 48)
            {
                int n = midi;
                for (int g = 0; g < 6 && f == null; g++)
                {
                    n += 12;
                    folded = true;
                    f = Map(n);
                }
            }
            else if (midi > 85)
            {
                int n = midi;
                for (int g = 0; g < 6 && f == null; g++)
                {
                    n -= 12;
                    folded = true;
                    f = Map(n);
                }
            }
            return f;
        }

        public static string GuessRole(ChannelInfo ci)
        {
            if (ci.MinMidi < 48) return "低音口琴";
            if (ci.StartMs <= 80 && ci.Count > 80 && ci.Count < 220 && ci.MaxMidi <= 84) return "主旋律";
            if (ci.MaxMidi >= 84) return "14孔高声部";
            if (ci.StartMs >= 30000) return "后段";
            return "第二声部";
        }

        public static string ChannelLine(ChannelInfo ci, int transpose, bool fold)
        {
            int playable = 0;
            int total = ci.Count;
            // InRange is at transpose 0; live counts shown after rebuild.
            playable = ci.InRange;
            int miss = total - playable;
            string pct = total <= 0 ? "0%" : ((playable * 100) / total).ToString() + "%";
            string missTxt = miss <= 0 ? "可奏" + pct : ("超范围" + miss.ToString());
            string role = ci.Role == null ? "" : ci.Role;
            return "CH" + (ci.Ch + 1).ToString() + "  " + total.ToString() + "音  "
                + NoteName(ci.MinMidi) + "-" + NoteName(ci.MaxMidi) + "  " + missTxt + "  " + role;
        }

        public static string LayerText(bool left, bool mid, bool right)
        {
            if (left && mid) return "左+中 低八度升半音";
            if (right && mid) return "右+中 高八度升半音";
            if (left) return "左键 低八度";
            if (right) return "右键 高八度";
            if (mid) return "中键 升半音";
            return "不按鼠标 基准八度";
        }

        public static string MouseShort(bool left, bool mid, bool right)
        {
            string s = "";
            if (left) s += "左";
            if (mid) s += "中";
            if (right) s += "右";
            return s.Length == 0 ? "-" : s;
        }

        public static int HoleFromKey(Keys k)
        {
            if (k == Keys.Z) return 0;
            if (k == Keys.X) return 1;
            if (k == Keys.C) return 2;
            if (k == Keys.V) return 3;
            if (k == Keys.B) return 4;
            if (k == Keys.N) return 5;
            if (k == Keys.M) return 6;
            if (k == Keys.Oemcomma) return 7;
            return -1;
        }
    }

    public class RawEv
    {
        public long Tick;
        public byte Status;
        public byte D1;
        public byte D2;
        public int Track;
    }

    public class TempoEv
    {
        public long Tick;
        public long UsPerQ;
    }

    public static class MidiParser
    {
        public static List<MidiNote> Parse(string path, out long totalMs, out List<ChannelInfo> channels)
        {
            List<MidiNote> notes = new List<MidiNote>();
            channels = new List<ChannelInfo>();
            totalMs = 0;

            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 14) throw new Exception("文件太短，不是有效 MIDI。");
            data = UnwrapRiff(data);
            if (data.Length < 14) throw new Exception("文件太短，不是有效 MIDI。");
            if (ReadFourCC(data, 0) != "MThd") throw new Exception("找不到 MThd 头。");
            int headerLen = ReadI32(data, 4);
            int trackCount = ReadI16(data, 10);
            if (trackCount < 0) trackCount = 0;
            if (trackCount > 256) trackCount = 256;
            int division = ReadI16(data, 12);
            if (division <= 0) division = 480;

            int pos = 8 + headerLen;
            List<RawEv> raw = new List<RawEv>();
            List<TempoEv> tempos = new List<TempoEv>();
            Dictionary<int, string> trackNames = new Dictionary<int, string>();

            for (int t = 0; t < trackCount && pos + 8 <= data.Length; t++)
            {
                string trackName = "";
                if (ReadFourCC(data, pos) != "MTrk")
                {
                    int skipLen = ReadI32(data, pos + 4);
                    pos += 8 + skipLen;
                    continue;
                }
                int trackLen = ReadI32(data, pos + 4);
                int trackEnd = pos + 8 + trackLen;
                int p = pos + 8;
                long tick = 0;
                byte running = 0;
                while (p < trackEnd)
                {
                    int delta = ReadVar(data, ref p);
                    tick += delta;
                    if (p >= trackEnd) break;
                    byte status = data[p];
                    if (status < 0x80) status = running;
                    else
                    {
                        p++;
                        if (status >= 0x80 && status < 0xf0) running = status;
                    }
                    if (status == 0xff)
                    {
                        if (p >= trackEnd) break;
                        byte meta = data[p++];
                        int len = ReadVar(data, ref p);
                        if (meta == 0x51 && len >= 3 && p + 3 <= trackEnd)
                        {
                            long us = (data[p] << 16) | (data[p + 1] << 8) | data[p + 2];
                            TempoEv te = new TempoEv();
                            te.Tick = tick;
                            te.UsPerQ = us;
                            tempos.Add(te);
                        }
                        if ((meta == 0x03 || meta == 0x04) && len > 0 && p + len <= trackEnd)
                        {
                            string nm = DecodeText(data, p, len);
                            if (nm != null) nm = nm.Trim('\0', '\r', '\n', ' ');
                            if (!string.IsNullOrEmpty(nm)) trackName = nm;
                        }
                        p += len;
                    }
                    else if (status == 0xf0 || status == 0xf7)
                    {
                        int len = ReadVar(data, ref p);
                        p += len;
                    }
                    else if (status >= 0x80 && status < 0xf0)
                    {
                        byte high = (byte)(status & 0xf0);
                        int need = 2;
                        if (high == 0xc0 || high == 0xd0) need = 1;
                        if (p + need > trackEnd) break;
                        byte d1 = data[p++];
                        byte d2 = 0;
                        if (need == 2) d2 = data[p++];
                        if (high == 0x90 || high == 0x80)
                        {
                            RawEv re = new RawEv();
                            re.Tick = tick;
                            re.Status = status;
                            re.D1 = d1;
                            re.D2 = d2;
                            re.Track = t + 1;
                            if (high == 0x90 && d2 == 0) re.Status = (byte)(0x80 | (status & 0x0f));
                            raw.Add(re);
                        }
                    }
                }
                if (trackName != null && trackName.Length > 0)
                    trackNames[t + 1] = trackName;
                pos = trackEnd;
            }

            tempos.Sort(delegate(TempoEv a, TempoEv b) { return a.Tick.CompareTo(b.Tick); });
            raw.Sort(delegate(RawEv a, RawEv b) { return a.Tick.CompareTo(b.Tick); });

            Dictionary<int, List<MidiNote>> held = new Dictionary<int, List<MidiNote>>();
            foreach (RawEv re in raw)
            {
                int ms = TickToMs(re.Tick, tempos, division);
                int ch = re.Status & 0x0f;
                bool on = (re.Status & 0xf0) == 0x90 && re.D2 > 0;
                // Pair note-off with note-on inside the same (track, channel) so that
                // two tracks sharing one channel cannot steal each other's note length.
                int heldKey = (re.Track << 8) | ch;
                if (!held.ContainsKey(heldKey)) held[heldKey] = new List<MidiNote>();
                if (on)
                {
                    MidiNote n = new MidiNote();
                    n.StartMs = ms;
                    n.Midi = re.D1;
                    n.Vel = re.D2;
                    n.Ch = ch;
                    n.Track = re.Track;
                    held[heldKey].Add(n);
                }
                else
                {
                    List<MidiNote> list = held[heldKey];
                    bool matched = false;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i].Midi == re.D1)
                        {
                            list[i].EndMs = ms;
                            notes.Add(list[i]);
                            list.RemoveAt(i);
                            matched = true;
                            break;
                        }
                    }
                    // Tolerate a stray note-off that arrives in a different track.
                    if (!matched)
                    {
                        for (int t2 = 0; t2 < 64 && !matched; t2++)
                        {
                            int alt = (t2 << 8) | ch;
                            if (alt == heldKey || !held.ContainsKey(alt)) continue;
                            List<MidiNote> altList = held[alt];
                            for (int i = altList.Count - 1; i >= 0; i--)
                            {
                                if (altList[i].Midi == re.D1)
                                {
                                    altList[i].EndMs = ms;
                                    notes.Add(altList[i]);
                                    altList.RemoveAt(i);
                                    matched = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                if (ms > totalMs) totalMs = ms;
            }

            notes.Sort(delegate(MidiNote a, MidiNote b)
            {
                int c = a.StartMs.CompareTo(b.StartMs);
                if (c != 0) return c;
                c = a.Ch.CompareTo(b.Ch);
                if (c != 0) return c;
                return b.Midi.CompareTo(a.Midi);
            });

            Dictionary<int, ChannelInfo> map = new Dictionary<int, ChannelInfo>();
            for (int i = 0; i < notes.Count; i++)
            {
                MidiNote n = notes[i];
                int key = (n.Track << 8) | n.Ch;
                if (!map.ContainsKey(key))
                {
                    ChannelInfo ci = new ChannelInfo();
                    ci.Ch = n.Ch;
                    ci.Track = n.Track;
                    ci.MinMidi = 127;
                    ci.MaxMidi = 0;
                    ci.StartMs = n.StartMs;
                    ci.IsDrum = (n.Ch == 9);   // 通道 10 = 打击乐
                    if (trackNames.ContainsKey(n.Track) && trackNames[n.Track] != null && trackNames[n.Track].Length > 0)
                        ci.Name = trackNames[n.Track];
                    else
                        ci.Name = "\u58f0\u9053 " + (n.Ch + 1).ToString();
                    map[key] = ci;
                }
                ChannelInfo cinfo = map[key];
                cinfo.Count++;
                if (n.Midi < cinfo.MinMidi) cinfo.MinMidi = n.Midi;
                if (n.Midi > cinfo.MaxMidi) cinfo.MaxMidi = n.Midi;
                if (n.Midi >= 48 && n.Midi <= 85) cinfo.InRange++;
                if (n.StartMs < cinfo.StartMs) cinfo.StartMs = n.StartMs;
            }
            for (int i = 0; i < notes.Count; i++)
            {
                int key = (notes[i].Track << 8) | notes[i].Ch;
                for (int j = i + 1; j < notes.Count; j++)
                {
                    if (notes[j].StartMs >= notes[i].EndMs) break;
                    if (notes[j].Ch == notes[i].Ch && notes[j].Track == notes[i].Track && notes[j].StartMs < notes[i].EndMs)
                        map[key].Overlaps++;
                }
            }
            foreach (ChannelInfo ci in map.Values)
            {
                ci.Score = ci.InRange;
                if (ci.Overlaps == 0) ci.Score += 1000;
                else ci.Score -= ci.Overlaps * 8;
                ci.Score -= ci.StartMs / 40;
                if (ci.StartMs <= 50) ci.Score += 250;
                if (ci.MinMidi >= 53) ci.Score += 60;
                if (ci.MaxMidi <= 52) ci.Score -= 80;
                if (ci.Count > 250) ci.Score -= 40;
                if (ci.IsDrum)
                {
                    ci.Score = int.MinValue / 2;   // 打击乐轨永远排在最后，不会被自动选为主旋律
                    ci.Role = "打击乐轨";
                }
                else
                {
                    ci.Role = Util.GuessRole(ci);
                }
                channels.Add(ci);
            }
            channels.Sort(delegate(ChannelInfo a, ChannelInfo b) { return b.Score.CompareTo(a.Score); });
            return notes;
        }

        // RIFF RMID (.rmi) and some .kar files wrap the SMF inside a RIFF chunk list.
        // Find the "data" chunk and return the SMF payload it holds.
        static byte[] UnwrapRiff(byte[] b)
        {
            if (b.Length < 12) return b;
            if (ReadFourCC(b, 0) != "RIFF") return b;
            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = ReadFourCC(b, p);
                int len = ReadI32(b, p + 4);
                if (len < 0 || p + 8 + len > b.Length) break;
                if (id == "data")
                {
                    byte[] sub = new byte[len];
                    Array.Copy(b, p + 8, sub, 0, len);
                    return sub;
                }
                p += 8 + len;
                if ((len & 1) == 1) p++;
            }
            return b;
        }

        static int TickToMs(long tick, List<TempoEv> tempos, int div)
        {
            long usPerQ = 500000;
            long lastTick = 0;
            long lastUs = 0;
            int i = 0;
            while (i < tempos.Count && tempos[i].Tick <= tick)
            {
                lastUs += (tempos[i].Tick - lastTick) * usPerQ / div;
                lastTick = tempos[i].Tick;
                usPerQ = tempos[i].UsPerQ;
                i++;
            }
            long us = lastUs + (tick - lastTick) * usPerQ / div;
            return (int)(us / 1000);
        }

        // MIDI meta text (track name, marker) is supposed to be ASCII but in the
        // wild it is frequently UTF-8 or the local ANSI code page. Encoding.Default
        // alone mangles UTF-8 track names into mojibake such as 浣庨煶鍙ｇ惔 for
        // 低音口琴, so probe for well-formed UTF-8 first and fall back to GBK.
        static string DecodeText(byte[] b, int pos, int len)
        {
            if (b == null || len <= 0 || pos < 0 || pos + len > b.Length) return "";
            bool ascii = true;
            for (int i = 0; i < len; i++)
            {
                if (b[pos + i] >= 0x80) { ascii = false; break; }
            }
            if (ascii) return Encoding.ASCII.GetString(b, pos, len);

            if (IsValidUtf8(b, pos, len))
            {
                try { return Encoding.UTF8.GetString(b, pos, len); }
                catch { }
            }
            try { return Encoding.GetEncoding(936).GetString(b, pos, len); }
            catch { }
            try { return Encoding.Default.GetString(b, pos, len); }
            catch { }
            return "";
        }

        static bool IsValidUtf8(byte[] b, int pos, int len)
        {
            int i = pos, end = pos + len;
            bool sawMultiByte = false;
            while (i < end)
            {
                byte c = b[i];
                if (c < 0x80) { i++; continue; }
                int need;
                if ((c & 0xE0) == 0xC0) need = 1;
                else if ((c & 0xF0) == 0xE0) need = 2;
                else if ((c & 0xF8) == 0xF0) need = 3;
                else return false;
                int cp = c & (0x7F >> need);
                if (i + need >= end) return false;
                for (int k = 1; k <= need; k++)
                {
                    byte cc = b[i + k];
                    if ((cc & 0xC0) != 0x80) return false;
                    cp = (cp << 6) | (cc & 0x3F);
                }
                // reject overlong / surrogate / out-of-range encodings
                if (need == 1 && cp < 0x80) return false;
                if (need == 2 && cp < 0x800) return false;
                if (need == 3 && (cp < 0x10000 || cp > 0x10FFFF)) return false;
                if (cp >= 0xD800 && cp <= 0xDFFF) return false;
                sawMultiByte = true;
                i += need + 1;
            }
            return sawMultiByte;
        }

        static string ReadFourCC(byte[] b, int pos)
        {
            if (pos + 4 > b.Length) return "";
            return Encoding.ASCII.GetString(b, pos, 4);
        }

        static int ReadI16(byte[] b, int pos) { return (b[pos] << 8) | b[pos + 1]; }
        static int ReadI32(byte[] b, int pos) { return (b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3]; }
        static int ReadVar(byte[] b, ref int pos)
        {
            int value = 0;
            byte c;
            do
            {
                if (pos >= b.Length) return value;
                c = b[pos++];
                value = (value << 7) | (c & 0x7f);
            } while ((c & 0x80) != 0);
            return value;
        }
    }

    public class MidiOut : IDisposable
    {
        [DllImport("winmm.dll")]
        static extern int midiOutOpen(out IntPtr handle, int deviceId, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")]
        static extern int midiOutClose(IntPtr handle);
        [DllImport("winmm.dll")]
        static extern int midiOutShortMsg(IntPtr handle, int msg);

        IntPtr handle = IntPtr.Zero;
        int curNote = -1;
        int volume = 110;
        public bool PianoMode;

        public MidiOut()
        {
            int r = midiOutOpen(out handle, -1, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) throw new Exception("无法打开 Windows MIDI 输出，错误码 " + r);
            SetHarp();
        }

        public void Short(int status, int d1, int d2)
        {
            if (handle == IntPtr.Zero) return;
            midiOutShortMsg(handle, status | (d1 << 8) | (d2 << 16));
        }

        public void SetVolume(int vol)
        {
            volume = vol;
            for (int ch = 0; ch < 16; ch++)
                Short(0xb0 | ch, 7, vol);
        }

        public void AllOff()
        {
            for (int ch = 0; ch < 16; ch++)
                Short(0xb0 | ch, 123, 0);
            curNote = -1;
        }

        public void SetHarp()
        {
            AllOff();
            PianoMode = false;
            Short(0xc0, 22, 0);
            Short(0xb0, 7, volume);
            Short(0xb0, 10, 64);
            Short(0xb0, 91, 48);
            Short(0xb0, 93, 20);
            Short(0xb0, 72, 20);
            Short(0xb0, 73, 16);
        }

        public void SetPiano()
        {
            AllOff();
            PianoMode = true;
            for (int ch = 0; ch < 16; ch++)
            {
                Short(0xc0 | ch, 0, 0);
                Short(0xb0 | ch, 7, volume);
                Short(0xb0 | ch, 10, 64);
                Short(0xb0 | ch, 91, 40);
                Short(0xb0 | ch, 93, 0);
            }
        }

        public void NoteOn(int midi, int vel)
        {
            if (midi < 0 || midi > 127) return;
            if (curNote >= 0) Short(0x80, curNote, 0);
            if (vel < 36) vel = 36;
            if (vel > 120) vel = 120;
            Short(0x90, midi, vel);
            curNote = midi;
        }

        public void NoteOff()
        {
            if (curNote >= 0)
            {
                Short(0x80, curNote, 0);
                Short(0xb0, 123, 0);
                curNote = -1;
            }
            else
            {
                AllOff();
            }
        }

        public void NoteOnCh(int ch, int midi, int vel)
        {
            if (midi < 0 || midi > 127) return;
            if (vel < 1) vel = 1;
            if (vel > 127) vel = 127;
            Short(0x90 | (ch & 15), midi, vel);
        }

        public void NoteOffCh(int ch, int midi)
        {
            if (midi < 0 || midi > 127) return;
            Short(0x80 | (ch & 15), midi, 0);
        }

        public void Dispose()
        {
            AllOff();
            if (handle != IntPtr.Zero)
            {
                midiOutClose(handle);
                handle = IntPtr.Zero;
            }
        }
    }
}


namespace DeltaHarp
{
    public static class SongBuilder
    {
        public static List<MidiNote> Build(List<MidiNote> all, int ch, int transpose, bool fold)
        {
            return Build(all, new int[] { ch }, transpose, fold, true, 0);
        }

        public static List<MidiNote> Build(List<MidiNote> all, int[] chs, int transpose, bool fold, bool takeHighest, int breathMs)
        {
            List<ChannelInfo> parts = new List<ChannelInfo>();
            if (chs != null)
            {
                for (int i = 0; i < chs.Length; i++)
                {
                    ChannelInfo ci = new ChannelInfo();
                    ci.Ch = chs[i];
                    ci.Track = -1;
                    parts.Add(ci);
                }
            }
            return Build(all, parts.ToArray(), transpose, fold, takeHighest, breathMs);
        }

        static int PriKey(MidiNote n, bool useTrack)
        {
            return useTrack ? ((n.Track << 8) | n.Ch) : n.Ch;
        }

        static int PriKey(ChannelInfo p, bool useTrack)
        {
            return useTrack ? ((p.Track << 8) | p.Ch) : p.Ch;
        }

        public static List<MidiNote> Build(List<MidiNote> all, ChannelInfo[] parts, int transpose, bool fold, bool takeHighest, int breathMs)
        {
            int ignored;
            return Build(all, parts, transpose, fold, takeHighest, breathMs, false, out ignored);
        }

        /// <summary>
        /// trimLead=true 时剪掉开头空拍，让第一个音从 0 秒开始（音间相对时值不变）。
        /// trimmedMs 回传被剪掉的毫秒数，供界面显示。
        /// </summary>
        public static List<MidiNote> Build(List<MidiNote> all, ChannelInfo[] parts, int transpose, bool fold, bool takeHighest, int breathMs, bool trimLead, out int trimmedMs)
        {
            trimmedMs = 0;
            Dictionary<int, int> pri = new Dictionary<int, int>();
            bool useTrack = false;
            if (parts != null)
            {
                for (int i = 0; i < parts.Length; i++)
                    if (parts[i] != null && parts[i].Track > 0) useTrack = true;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == null) continue;
                    int key = PriKey(parts[i], useTrack);
                    if (!pri.ContainsKey(key)) pri[key] = i;
                }
            }
            List<MidiNote> src = new List<MidiNote>();
            foreach (MidiNote n in all)
            {
                if (pri.Count > 0 && !pri.ContainsKey(PriKey(n, useTrack))) continue;
                if (n.EndMs <= n.StartMs) continue;
                MidiNote c = new MidiNote();
                c.StartMs = n.StartMs;
                c.EndMs = n.EndMs;
                c.Midi = n.Midi + transpose;
                c.Vel = n.Vel;
                c.Ch = n.Ch;
                src.Add(c);
            }
            src.Sort(delegate(MidiNote a, MidiNote b)
            {
                int cmp = a.StartMs.CompareTo(b.StartMs);
                if (cmp != 0) return cmp;
                if (takeHighest)
                {
                    cmp = b.Midi.CompareTo(a.Midi);
                    if (cmp != 0) return cmp;
                }
                int pa = pri.ContainsKey(PriKey(a, useTrack)) ? pri[PriKey(a, useTrack)] : 99;
                int pb = pri.ContainsKey(PriKey(b, useTrack)) ? pri[PriKey(b, useTrack)] : 99;
                return pa.CompareTo(pb);
            });

            List<MidiNote> melody = new List<MidiNote>();
            for (int i = 0; i < src.Count; i++)
            {
                if (melody.Count > 0 && melody[melody.Count - 1].StartMs == src[i].StartMs)
                    continue;
                melody.Add(src[i]);
            }

            List<MidiNote> outNotes = new List<MidiNote>();
            for (int i = 0; i < melody.Count; i++)
            {
                MidiNote n = melody[i];
                int cut = n.EndMs;
                if (i + 1 < melody.Count && melody[i + 1].StartMs < cut)
                    cut = melody[i + 1].StartMs;
                int sound = cut - n.StartMs;
                if (sound < 1) continue;
                bool folded = false;
                Finger f = Util.Map(n.Midi);
                if (f == null && fold)
                    f = Util.MapFold(n.Midi, out folded);
                if (breathMs > 0 && sound > breathMs + 20)
                    sound -= breathMs;
                n.SoundMs = sound;
                n.Fing = f;
                n.Folded = folded && f != null;
                n.Unplayable = f == null;
                outNotes.Add(n);
            }

            // 去除开头空拍：整体前移，音之间相对时值完全不变（只影响起点）
            if (trimLead && outNotes.Count > 0)
            {
                int first = outNotes[0].StartMs;
                if (first > 0)
                {
                    trimmedMs = first;
                    for (int i = 0; i < outNotes.Count; i++)
                    {
                        outNotes[i].StartMs -= first;
                        if (outNotes[i].StartMs < 0) outNotes[i].StartMs = 0;
                    }
                }
            }
            return outNotes;
        }

        public static List<MidiNote> PlayableOnly(List<MidiNote> notes)
        {
            List<MidiNote> list = new List<MidiNote>();
            foreach (MidiNote n in notes)
            {
                if (n.Fing != null && !n.Unplayable) list.Add(n);
            }
            return list;
        }

        /// <summary>
        /// 按输入时序预算把音符编成物理事件表（对应原版 HarpAutoPlayer 的 BuildSchedule）。
        /// 关键点：
        ///   1. 修饰键必须比音键早 ModLeadMs 发出，否则同帧被采样会漏音/变调；
        ///   2. 每个音键至少要按住 MinHoldMs，跨过一个采样帧点，否则目标程序读不到；
        ///   3. 同一根键两次按下要间隔 RetriggerMs，跨过"抬起"那一帧；
        ///   4. 口琴是单音，前音没抬完的音顺延到前音之后（"槽位"），绝不压缩成零时长。
        /// </summary>
        public static List<MacroStep> MakeMacro(List<MidiNote> notes)
        {
            return MakeMacro(notes, InputProfile.Standard());
        }

        public static List<MacroStep> MakeMacro(List<MidiNote> notes, InputProfile prof)
        {
            if (prof == null) prof = InputProfile.Standard();
            List<MacroStep> steps = new List<MacroStep>();
            if (notes == null || notes.Count == 0) return steps;

            int frame = prof.FrameMs;
            int modLead = Math.Max(prof.ModLeadMs, frame);
            int retrig = Math.Max(prof.RetriggerMs, frame);
            // 最短按住要比一帧再多 1ms：恰好一帧时若按下正好落在帧边界上，
            // 整个按住区间可能一个帧点都不含，目标程序就整段读不到。
            int minHold = Math.Max(prof.MinHoldMs, frame + 1);

            // 修饰键状态机
            bool heldLeft = false, heldMid = false, heldRight = false;
            string heldKey = null;
            int heldDownAt = 0;
            int heldUpAt = 0;
            Dictionary<string, int> lastDown = new Dictionary<string, int>();
            int slotStart = 0;

            List<MidiNote> ordered = new List<MidiNote>(notes);
            ordered.Sort(delegate(MidiNote a, MidiNote b)
            {
                int c = a.StartMs.CompareTo(b.StartMs);
                if (c != 0) return c;
                return a.SoundMs.CompareTo(b.SoundMs);
            });

            for (int i = 0; i < ordered.Count; i++)
            {
                MidiNote n = ordered[i];
                Finger f = n.Fing;
                if (f == null) continue;

                int baseStart = Math.Max(0, n.StartMs);
                int duration = Math.Max(1, n.SoundMs);
                int endT = baseStart + duration;

                // ① 本音最早能按下的时刻
                int downT = Math.Max(baseStart, slotStart);
                if (heldKey != null && downT < heldDownAt + minHold)
                    downT = heldDownAt + minHold;
                endT = downT + duration;

                // ② 同一根键的重触发间隔
                int prevDown;
                if (lastDown.TryGetValue(f.Key, out prevDown) && downT < prevDown + retrig)
                    downT = prevDown + retrig;
                if (lastDown.TryGetValue(f.Key, out prevDown) && downT > endT)
                {
                    int avail = Math.Max(0, endT - prevDown);
                    int effRetrig = Math.Max(frame, Math.Min(retrig, avail));
                    downT = Math.Max(Math.Max(baseStart, slotStart), Math.Min(endT, prevDown + effRetrig));
                    endT = downT + duration;
                }

                // ③ 前音抬起：最早它的谱面结束时刻，最晚本音按下时刻
                if (heldKey != null)
                {
                    int upT = Math.Min(heldUpAt, downT);
                    if (upT < heldDownAt + minHold) upT = Math.Min(downT, heldDownAt + minHold);
                    Add(steps, upT, "KeyUp", heldKey, n, HoleOf(f));
                    if (upT > slotStart) slotStart = upT;
                    heldKey = null;
                }

                // ④ 修饰键切换：提前 modLead 发出
                if (f.Left != heldLeft || f.Middle != heldMid || f.Right != heldRight)
                {
                    int modT = Math.Max(0, downT - modLead);
                    // 先松开所有不该按的，再按下所有该按的（顺序固定，不依赖排序稳定性）
                    if (heldLeft && !f.Left) { Add(steps, modT, "MouseUp", "Left", n, f.Hole); heldLeft = false; }
                    if (heldRight && !f.Right) { Add(steps, modT, "MouseUp", "Right", n, f.Hole); heldRight = false; }
                    if (heldMid && !f.Middle) { Add(steps, modT, "MouseUp", "Middle", n, f.Hole); heldMid = false; }
                    if (f.Left && !heldLeft) { Add(steps, modT, "MouseDown", "Left", n, f.Hole); heldLeft = true; }
                    if (f.Right && !heldRight) { Add(steps, modT, "MouseDown", "Right", n, f.Hole); heldRight = true; }
                    if (f.Middle && !heldMid) { Add(steps, modT, "MouseDown", "Middle", n, f.Hole); heldMid = true; }
                    if (downT < modT + frame) downT = modT + frame;
                    endT = downT + duration;
                }

                Add(steps, downT, "KeyDown", f.Key, n, f.Hole);
                heldKey = f.Key;
                heldDownAt = downT;
                heldUpAt = endT;
                lastDown[f.Key] = downT;
            }

            if (heldKey != null)
            {
                int upT = Math.Max(heldUpAt, heldDownAt + minHold);
                Add(steps, upT, "KeyUp", heldKey, null, HoleOfByName(heldKey));
                heldKey = null;
            }
            int tail = 0;
            if (ordered.Count > 0)
                tail = ordered[ordered.Count - 1].StartMs + ordered[ordered.Count - 1].SoundMs + 30;
            if (heldLeft) Add(steps, tail, "MouseUp", "Left", null, -1);
            if (heldMid) Add(steps, tail, "MouseUp", "Middle", null, -1);
            if (heldRight) Add(steps, tail, "MouseUp", "Right", null, -1);

            steps.Sort(delegate(MacroStep a, MacroStep b)
            {
                int c = a.AbsMs.CompareTo(b.AbsMs);
                if (c != 0) return c;
                return ActionRank(a.Action).CompareTo(ActionRank(b.Action));
            });
            int prevAbs = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                steps[i].DelayMs = steps[i].AbsMs - prevAbs;
                if (steps[i].DelayMs < 0) steps[i].DelayMs = 0;
                prevAbs = steps[i].AbsMs;
            }
            return steps;
        }

        static int HoleOf(Finger f)
        {
            return f == null ? -1 : f.Hole;
        }

        static int HoleOfByName(string key)
        {
            if (key == null) return -1;
            for (int i = 0; i < Util.KeyNames.Length; i++)
                if (Util.KeyNames[i] == key) return i;
            return -1;
        }

        static int ActionRank(string a)
        {
            if (a == "KeyUp") return 0;
            if (a == "MouseUp") return 1;
            if (a == "MouseDown") return 2;
            if (a == "KeyDown") return 3;
            return 4;
        }

        static void Add(List<MacroStep> steps, int abs, string action, string code, MidiNote n, int hole)
        {
            MacroStep s = new MacroStep();
            s.AbsMs = abs;
            s.Action = action;
            s.Code = code;
            s.Hole = hole;
            if (n != null && n.Fing != null)
            {
                s.Note = n.Fing.Name;
                s.Layer = n.Fing.Layer;
            }
            else
            {
                s.Note = "";
                s.Layer = "";
            }
            steps.Add(s);
        }
    }

    public static class Exporter
    {
        public static void WriteAll(string dir, string title, List<MidiNote> notes, List<MacroStep> steps, ChannelInfo ch, int transpose)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            WriteScore(Path.Combine(dir, "乐谱-" + Safe(title) + ".txt"), title, notes, ch, transpose);
            WriteCsv(Path.Combine(dir, "macro-razer.csv"), steps);
            WriteXml(Path.Combine(dir, "macro-razer.xml"), title, steps);
            WriteLua(Path.Combine(dir, "macro-logitech.lua"), title, steps);
            WriteKeyTable(Path.Combine(dir, "按键表.csv"), steps);
            WriteGuide(Path.Combine(dir, "使用说明.txt"));
            // 把当前（可能被卷帘编辑过的）曲子本身也写成 MIDI，
            // 这样卷帘里改了音符，导出的 .mid 就是改过之后的结果。
            WriteMidi(Path.Combine(dir, Safe(title) + "-口琴.mid"), title, notes);
        }

        /// <summary>
        /// 把当前音符表写成标准 MIDI 文件（SMF format 0，480 分音符，120 BPM）。
        /// 对应原版 v2.0.0 的「导出 MIDI」；卷帘编辑后以此为准。
        /// </summary>
        public static void WriteMidi(string path, string title, List<MidiNote> notes)
        {
            const int ppq = 480;
            // 120 BPM：一个四分音符 = 500ms，所以 1ms = ppq/500 tick
            List<int[]> ev = new List<int[]>();
            for (int i = 0; i < notes.Count; i++)
            {
                MidiNote n = notes[i];
                int midi = n.Midi;
                if (midi < 0) midi = 0;
                if (midi > 127) midi = 127;
                int vel = n.Vel;
                if (vel < 1) vel = 80;
                if (vel > 127) vel = 127;
                int t0 = (int)((long)Math.Max(0, n.StartMs) * ppq / 500);
                int len = Math.Max(1, n.SoundMs);
                int t1 = (int)((long)(Math.Max(0, n.StartMs) + len) * ppq / 500);
                if (t1 <= t0) t1 = t0 + 1;
                ev.Add(new int[] { t0, 1, midi, vel });
                ev.Add(new int[] { t1, 0, midi, 0 });
            }
            ev.Sort(delegate(int[] a, int[] b)
            {
                if (a[0] != b[0]) return a[0].CompareTo(b[0]);
                return a[1].CompareTo(b[1]);   // 同一时刻先关后开
            });

            MemoryStream body = new MemoryStream();
            WriteVarLen(body, 0);
            WriteBytes(body, new byte[] { 0xFF, 0x03 });
            byte[] name = Encoding.ASCII.GetBytes("Harmonica");
            WriteVarLen(body, name.Length);
            body.Write(name, 0, name.Length);
            // 速度：120 BPM
            WriteVarLen(body, 0);
            WriteBytes(body, new byte[] { 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20 });

            int last = 0;
            for (int i = 0; i < ev.Count; i++)
            {
                int[] e = ev[i];
                WriteVarLen(body, Math.Max(0, e[0] - last));
                last = e[0];
                byte status = (byte)(e[1] == 1 ? 0x90 : 0x80);
                body.WriteByte(status);
                body.WriteByte((byte)e[2]);
                body.WriteByte((byte)e[3]);
            }
            WriteVarLen(body, 0);
            WriteBytes(body, new byte[] { 0xFF, 0x2F, 0x00 });

            byte[] payload = body.ToArray();
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                WriteAscii(w, "MThd");
                WriteBE32(w, 6);
                WriteBE16(w, 0);          // format 0
                WriteBE16(w, 1);          // 1 track
                WriteBE16(w, ppq);
                WriteAscii(w, "MTrk");
                WriteBE32(w, payload.Length);
                w.Write(payload);
            }
        }

        static void WriteAscii(BinaryWriter w, string s)
        {
            for (int i = 0; i < s.Length; i++) w.Write((byte)s[i]);
        }
        static void WriteBE32(BinaryWriter w, int v)
        {
            w.Write((byte)((v >> 24) & 0xFF)); w.Write((byte)((v >> 16) & 0xFF));
            w.Write((byte)((v >> 8) & 0xFF)); w.Write((byte)(v & 0xFF));
        }
        static void WriteBE16(BinaryWriter w, int v)
        {
            w.Write((byte)((v >> 8) & 0xFF)); w.Write((byte)(v & 0xFF));
        }
        static void WriteBytes(Stream s, byte[] b) { s.Write(b, 0, b.Length); }
        static void WriteVarLen(Stream s, int value)
        {
            if (value < 0) value = 0;
            int buffer = value & 0x7F;
            while ((value >>= 7) > 0)
            {
                buffer <<= 8;
                buffer |= 0x80;
                buffer += (value & 0x7F);
            }
            while (true)
            {
                s.WriteByte((byte)(buffer & 0xFF));
                if ((buffer & 0x80) != 0) buffer >>= 8; else break;
            }
        }

        static string Safe(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        static void WriteScore(string path, string title, List<MidiNote> notes, ChannelInfo ch, int transpose)
        {
            StringBuilder sb = new StringBuilder();
            int playable = 0, miss = 0, folded = 0;
            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i].Unplayable || notes[i].Fing == null) miss++;
                else playable++;
                if (notes[i].Folded) folded++;
            }
            sb.AppendLine("8 孔口琴 · 按键乐谱");
            sb.AppendLine("曲名：" + title);
            sb.AppendLine("说明：由 MIDI 主旋律转换，仅供个人练习，曲谱版权归原作者。");
            sb.AppendLine("重要：这首 MIDI 若是 14 孔半音口琴或钢琴总谱，音域会超出 8 孔口琴范围。");
            sb.AppendLine("8 孔音域 C3–C#6。移调=0 保持原调。八度折叠已开启：超范围音自动折到最近八度，不丢音。");
            sb.AppendLine("键位：Z X C V B N M , （逗号=最高音 do / C5，不按鼠标）");
            sb.AppendLine("鼠标：不按=基准八度 | 左键=低八度 | 右键=高八度 | 中键=升半音（可与左/右叠加）");
            if (ch != null)
                sb.AppendLine("MIDI通道：" + (ch.Ch + 1).ToString() + "  " + (ch.Role == null ? "" : ch.Role) + "    移调：" + transpose.ToString() + "（0=原调）");
            int last = 0;
            if (notes.Count > 0) last = notes[notes.Count - 1].StartMs + notes[notes.Count - 1].SoundMs;
            sb.AppendLine("音符数：" + notes.Count.ToString() + "    可奏：" + playable.ToString() + "    按不到：" + miss.ToString() + "    八度折叠：" + folded.ToString());
            sb.AppendLine("总时长：" + (last / 1000.0).ToString("0.00") + " 秒    导出宏只含可奏音");
            sb.AppendLine();
            sb.AppendLine("序号\t开始(秒)\t时长(毫秒)\t按键\t鼠标\t音名\tMIDI\t层");
            for (int i = 0; i < notes.Count; i++)
            {
                MidiNote n = notes[i];
                Finger f = n.Fing;
                sb.Append((i + 1).ToString().PadLeft(4));
                sb.Append('\t');
                sb.Append((n.StartMs / 1000.0).ToString("0.000"));
                sb.Append('\t');
                sb.Append(n.SoundMs.ToString());
                sb.Append('\t');
                if (f == null)
                {
                    sb.Append("-\t-\t");
                    sb.Append(Util.NoteName(n.Midi));
                    sb.Append('\t');
                    sb.Append(n.Midi.ToString());
                    sb.Append('\t');
                    sb.Append("按不到(超出8孔原调音域)");
                }
                else
                {
                    sb.Append(f.Key);
                    sb.Append('\t');
                    sb.Append(Util.MouseShort(f.Left, f.Middle, f.Right));
                    sb.Append('\t');
                    sb.Append(f.Name);
                    sb.Append('\t');
                    sb.Append(n.Midi.ToString());
                    sb.Append('\t');
                    sb.Append(f.Layer);
                    if (n.Folded) sb.Append("  [八度折叠，音高已改八度]");
                }
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        static string CsvField(string s)
        {
            if (s == null) return "";
            bool quote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ',' || c == '"' || c == '\r' || c == '\n') { quote = true; break; }
            }
            if (!quote) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        static void WriteCsv(string path, List<MacroStep> steps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("index,abs_ms,delay_ms,action,code,note,hole,layer");
            for (int i = 0; i < steps.Count; i++)
            {
                MacroStep s = steps[i];
                sb.Append(i + 1);
                sb.Append(',');
                sb.Append(s.AbsMs);
                sb.Append(',');
                sb.Append(s.DelayMs);
                sb.Append(',');
                sb.Append(CsvField(s.Action));
                sb.Append(',');
                sb.Append(CsvField(s.Code));
                sb.Append(',');
                sb.Append(CsvField(s.Note));
                sb.Append(',');
                sb.Append(s.Hole);
                sb.Append(',');
                sb.Append(CsvField(s.Layer));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        static void WriteXml(string path, string title, List<MacroStep> steps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<Macro>");
            sb.AppendLine("  <Name>" + Xml(title) + "</Name>");
            sb.AppendLine("  <Comment>键盘选孔 + 鼠标左/中/右改调，不移动指针。导入失败时按 CSV 对照手动录入雷蛇宏。</Comment>");
            sb.AppendLine("  <MacroEvents>");
            foreach (MacroStep s in steps)
            {
                if (s.DelayMs > 0)
                {
                    sb.AppendLine("    <MacroEvent>");
                    sb.AppendLine("      <Type>2</Type>");
                    sb.AppendLine("      <DelayEvent><Delay>" + s.DelayMs.ToString() + "</Delay></DelayEvent>");
                    sb.AppendLine("    </MacroEvent>");
                }
                if (s.Action.StartsWith("Key"))
                {
                    int scan = ScanOf(s.Code);
                    int state = s.Action == "KeyDown" ? 0 : 1;
                    sb.AppendLine("    <MacroEvent>");
                    sb.AppendLine("      <Type>1</Type>");
                    sb.AppendLine("      <KeyEvent><Makecode>" + scan.ToString() + "</Makecode><State>" + state.ToString() + "</State></KeyEvent>");
                    sb.AppendLine("    </MacroEvent>");
                }
                else
                {
                    int btn = 1;
                    if (s.Code == "Right") btn = 2;
                    if (s.Code == "Middle") btn = 3;
                    int state = s.Action == "MouseDown" ? 0 : 1;
                    sb.AppendLine("    <MacroEvent>");
                    sb.AppendLine("      <Type>3</Type>");
                    sb.AppendLine("      <MouseEvent><MouseButton>" + btn.ToString() + "</MouseButton><State>" + state.ToString() + "</State></MouseEvent>");
                    sb.AppendLine("    </MacroEvent>");
                }
            }
            sb.AppendLine("  </MacroEvents>");
            sb.AppendLine("</Macro>");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        static int ScanOf(string key)
        {
            for (int i = 0; i < Util.KeyNames.Length; i++)
                if (Util.KeyNames[i] == key) return Util.Scan[i];
            return 0;
        }

        static string Xml(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        /// <summary>
        /// 罗技 G HUB 的 Lua 脚本。G HUB 官方脚本 API：
        /// PressKey / ReleaseKey / PressMouseButton / ReleaseMouseButton / Sleep。
        /// 按键名用字符串（"z".."comma"），鼠标键用 1=左 2=右 3=中。
        /// </summary>
        static void WriteLua(string path, string title, List<MacroStep> steps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("-- 由「口琴模拟器」导出的按键脚本（罗技 G HUB）");
            sb.AppendLine("-- 用法：G HUB → 选中设备 → 「游戏与应用程序」→ 添加目标程序 → 编写脚本 → 编辑脚本");
            sb.AppendLine("--       把本文件内容整段粘贴进去，保存；使用前先按下绑定的脚本触发键。");
            sb.AppendLine("-- 注意：G HUB 的 Lua 环境不保证暴露鼠标中键（3）。若升半音无效，请改用 CSV 交给别的工具。");
            if (!string.IsNullOrEmpty(title)) sb.AppendLine("-- 曲目：" + title);
            sb.AppendLine("-- 事件数：" + steps.Count.ToString());
            sb.AppendLine();
            sb.AppendLine("local events = {");
            int last = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                MacroStep s = steps[i];
                int wait = s.AbsMs - last;
                if (wait < 0) wait = 0;
                last = s.AbsMs;
                string action;
                if (s.Action != null && s.Action.StartsWith("Key"))
                {
                    action = (s.Action == "KeyDown" ? "PressKey(\"" : "ReleaseKey(\"")
                        + LuaKeyName(s.Code) + "\")";
                }
                else
                {
                    int btn = s.Code == "Right" ? 2 : (s.Code == "Middle" ? 3 : 1);
                    action = (s.Action == "MouseDown" ? "PressMouseButton(" : "ReleaseMouseButton(")
                        + btn.ToString() + ")";
                }
                sb.AppendLine("  {" + wait.ToString() + ", function() " + action + " end},");
            }
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("-- 触发方式：在 G HUB 里把本脚本绑定到某个 G 键或鼠标键，按下即开始演奏。");
            sb.AppendLine("function OnEvent(event, arg)");
            sb.AppendLine("  if event == \"G_PRESSED\" or event == \"MOUSE_BUTTON_PRESSED\" then");
            sb.AppendLine("    for i = 1, #events do");
            sb.AppendLine("      Sleep(events[i][1])");
            sb.AppendLine("      events[i][2]()");
            sb.AppendLine("    end");
            sb.AppendLine("  end");
            sb.AppendLine("end");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        static string LuaKeyName(string c)
        {
            if (c == ",") return "comma";
            if (string.IsNullOrEmpty(c)) return "";
            return c.ToLowerInvariant();
        }

        /// <summary>通用按键表：时刻 + 动作 + 目标，供其它宏工具使用。</summary>
        static void WriteKeyTable(string path, List<MacroStep> steps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("time_ms,action,target,note");
            for (int i = 0; i < steps.Count; i++)
            {
                MacroStep s = steps[i];
                string target;
                if (s.Action != null && s.Action.StartsWith("Key")) target = s.Code;
                else if (s.Code == "Left") target = "左键";
                else if (s.Code == "Right") target = "右键";
                else target = "中键";
                string action = (s.Action != null && s.Action.EndsWith("Down")) ? "down" : "up";
                sb.Append(s.AbsMs.ToString());
                sb.Append(',');
                sb.Append(action);
                sb.Append(',');
                sb.Append(CsvField(target));
                sb.Append(',');
                sb.Append(CsvField(s.Note));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        static void WriteGuide(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("口琴模拟器 使用说明");
            sb.AppendLine();
            sb.AppendLine("一、软件功能");
            sb.AppendLine("1. 桌面软件，内置 Windows GM 口琴音色（Program 22）。");
            sb.AppendLine("2. 「口琴映射」模式：单旋律，8 孔指法，GM 口琴音色。");
            sb.AppendLine("3. 「原曲试听」模式：全通道多声部，GM 钢琴音色，听原编曲。");
            sb.AppendLine();
            sb.AppendLine("二、缺音解决方案");
            sb.AppendLine("8 孔口琴音域：C3–C#6（MIDI 48–85）。");
            sb.AppendLine("键位：Z X C V B N M ,    鼠标：不按=基准 | 左=低八度 | 右=高八度 | 中=升半音");
            sb.AppendLine("「八度折叠」已默认开启：超范围音自动折到最近八度，整首不换调。");
            sb.AppendLine("例如 CH5 低音 F2 折到 F3，CH1 高音 D6 折到 D5，原调不变。");
            sb.AppendLine("所有通道开启折叠后均可 100% 播放，无缺音。");
            sb.AppendLine();
            sb.AppendLine("三、推荐用法");
            sb.AppendLine("《太阳照常升起》选 CH3 主旋律（F3–A5，145 音，原调 100% 可奏）。");
            sb.AppendLine("移调保持 0，八度折叠保持开启。");
            sb.AppendLine();
            sb.AppendLine("四、导出和雷蛇宏");
            sb.AppendLine("1. 点「导出乐谱/宏」→ 所有文件保存到 MIDI 文件夹。");
            sb.AppendLine("2. 导出内容：乐谱.txt + macro-razer.csv + macro-razer.xml + macro-logitech.lua + 按键表.csv + MIDI 文件。");
            sb.AppendLine("3. macro-razer.csv / macro-razer.xml 可导入雷蛇 Synapse 宏编辑器。");
            sb.AppendLine("4. macro-logitech.lua 是罗技 G HUB 脚本：G HUB → 编写脚本 → 整段粘贴 → 绑定触发键。");
            sb.AppendLine("5. 按键表.csv 是通用格式（时刻/动作/目标），给其它宏工具用。");
            sb.AppendLine("6. 空格播放/停止，Esc 停止。");
            sb.AppendLine();
            sb.AppendLine("五、输入兼容档位");
            sb.AppendLine("目标程序按键按帧采样。修饰键（鼠标左/中/右）必须比音键早一点按下，否则会被折进同一帧而漏音。");
            sb.AppendLine("「稳健」给 30fps / 卡顿留更多余量，漏音就换成它；「标准」是 60fps 推荐值；「极限」给高帧率用。");
            sb.AppendLine("导出的宏也会按当前档位的时序生成，保证和软件内演奏一致。");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

    }
}



namespace DeltaHarp
{
    /// <summary>
    /// 播放前自检（复刻原版 v2.0.4 PreflightCheck）：
    ///   1. 是否以管理员运行（未提权时，若目标程序是管理员，模拟按键会被 UIPI 拦截）；
    ///   2. 当前输入法是否处于中文/假名状态（这种状态会把字母键截走，导致"按了没反应"）。
    /// 判定原则：只有拿到硬证据才判"不通过"；读不准只提醒，绝不误拦。
    /// 说明：本类只用 .NET Framework 3.5 就能编译的老语法。
    /// </summary>
    public static class Preflight
    {
        public const int Pass = 0, Warn = 1, Fail = 2;

        public class Item
        {
            public string Name;
            public int Status;
            public string Detail;
            public Item(string n, int s, string d) { Name = n; Status = s; Detail = d; }
            public bool Passed { get { return Status == Pass; } }
        }

        public class Report
        {
            public Item Admin;
            public Item Ime;
            public bool AllPassed { get { return Admin.Passed && Ime.Passed; } }
            public bool HasBlocked { get { return Admin.Status == Fail || Ime.Status == Fail; } }
        }

        // 输入法状态：0=按键直达，1=中文/假名状态会截键，2=读不到
        public const int ImeDirect = 0, ImeNative = 1, ImeUnknown = 2;

        // ---------------- P/Invoke ----------------
        [StructLayout(LayoutKind.Sequential)]
        struct GUITHREADINFO
        {
            public int cbSize; public uint flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeoutMs, out IntPtr result);
        [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
        [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
        [DllImport("imm32.dll")] static extern bool ImmGetOpenStatus(IntPtr hIMC);
        [DllImport("imm32.dll")] static extern bool ImmGetConversionStatus(IntPtr hIMC, out uint conv, out uint sentence);
        [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr tokenHandle, int infoClass,
            out uint info, uint infoLen, out uint retLen);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);

        const uint TOKEN_QUERY = 0x0008;
        const int TokenElevation = 20;
        const uint WM_IME_CONTROL = 0x0283;
        const int IMC_GETOPENSTATUS = 0x0005;
        const int IMC_GETCONVERSIONMODE = 0x0001;
        const uint SMTO_ABORTIFHUNG = 0x0002, SMTO_BLOCK = 0x0001;
        const uint IME_CMODE_NATIVE = 0x0001;
        const uint IME_CMODE_KATAKANA = 0x0002;
        const uint IME_CMODE_FULLSHAPE = 0x0008;

        public static bool IsSelfElevated()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_QUERY, out token)) return false;
                uint elevated, retLen;
                if (!GetTokenInformation(token, TokenElevation, out elevated, sizeof(uint), out retLen)) return false;
                return elevated != 0;
            }
            catch { return false; }
            finally { if (token != IntPtr.Zero) CloseHandle(token); }
        }

        public static ushort LayoutIdOf(uint threadId)
        {
            try
            {
                IntPtr hkl = GetKeyboardLayout(threadId);
                if (hkl == IntPtr.Zero) return 0;
                return (ushort)(hkl.ToInt64() & 0xFFFF);
            }
            catch { return 0; }
        }

        public static bool IsCjkLayout(ushort lang)
        {
            return lang == 0x0804 || lang == 0x0404 || lang == 0x0411 || lang == 0x0412;
        }

        public static string LayoutName(ushort lang)
        {
            if (lang == 0x0804) return "简体中文";
            if (lang == 0x0404) return "繁体中文";
            if (lang == 0x0411) return "日文";
            if (lang == 0x0412) return "韩文";
            if (lang == 0x0409) return "英文";
            return "0x" + lang.ToString("X4");
        }

        static uint FocusedGuiThread(uint fallback)
        {
            try
            {
                GUITHREADINFO g = new GUITHREADINFO();
                g.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
                if (!GetGUIThreadInfo(fallback, ref g)) return 0;
                if (g.hwndFocus == IntPtr.Zero) return 0;
                uint pid;
                return GetWindowThreadProcessId(g.hwndFocus, out pid);
            }
            catch { return 0; }
        }

        static int ReadOpenStatus(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return -1;
            try
            {
                IntPtr o;
                IntPtr r = SendMessageTimeoutW(hWnd, WM_IME_CONTROL, (IntPtr)IMC_GETOPENSTATUS, IntPtr.Zero,
                    SMTO_ABORTIFHUNG | SMTO_BLOCK, 150, out o);
                if (r != IntPtr.Zero) return o != IntPtr.Zero ? 1 : 0;
            }
            catch { }
            IntPtr hImc = IntPtr.Zero;
            try
            {
                hImc = ImmGetContext(hWnd);
                if (hImc == IntPtr.Zero) return -1;
                return ImmGetOpenStatus(hImc) ? 1 : 0;
            }
            catch { return -1; }
            finally { if (hImc != IntPtr.Zero) ImmReleaseContext(hWnd, hImc); }
        }

        static bool? ReadAlphanumeric(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;
            IntPtr hImc = IntPtr.Zero;
            try
            {
                hImc = ImmGetContext(hWnd);
                if (hImc == IntPtr.Zero) return null;
                uint conv, sentence;
                if (!ImmGetConversionStatus(hImc, out conv, out sentence)) return null;
                return (conv & IME_CMODE_NATIVE) == 0;
            }
            catch { return null; }
            finally { if (hImc != IntPtr.Zero) ImmReleaseContext(hWnd, hImc); }
        }

        /// <summary>
        /// 最可靠来源：前台窗口的默认输入法窗口。
        /// 微软拼音中文态转换模式是 0x0401（含 IME_CMODE_NATIVE），英文态是 0x0000。
        /// 注意 IME_CMODE_ALPHANUMERIC 常量本身就是 0，不能拿它做位与判断。
        /// </summary>
        static bool ReadImeWindowState(IntPtr hWnd, out int open, out uint conv)
        {
            open = -1; conv = 0;
            if (hWnd == IntPtr.Zero) return false;
            try
            {
                IntPtr imeWnd = ImmGetDefaultIMEWnd(hWnd);
                if (imeWnd == IntPtr.Zero || imeWnd == hWnd) return false;
                IntPtr o1, o2;
                IntPtr r1 = SendMessageTimeoutW(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_GETOPENSTATUS, IntPtr.Zero,
                    SMTO_ABORTIFHUNG | SMTO_BLOCK, 150, out o1);
                if (r1 != IntPtr.Zero) open = o1 != IntPtr.Zero ? 1 : 0;
                IntPtr r2 = SendMessageTimeoutW(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_GETCONVERSIONMODE, IntPtr.Zero,
                    SMTO_ABORTIFHUNG | SMTO_BLOCK, 150, out o2);
                if (r2 != IntPtr.Zero) conv = (uint)o2.ToInt64();
                return true;
            }
            catch { return false; }
        }

        static bool IsAlphanumeric(uint conv) { return (conv & IME_CMODE_NATIVE) == 0; }

        public static int DetectImeMode(out string detail)
        {
            detail = "";
            IntPtr h = IntPtr.Zero;
            ushort windowLayout = 0;
            uint tid = 0;
            try
            {
                h = GetForegroundWindow();
                if (h != IntPtr.Zero)
                {
                    uint pid;
                    tid = GetWindowThreadProcessId(h, out pid);
                    windowLayout = LayoutIdOf(tid);
                }
            }
            catch { }

            uint focused = 0;
            ushort threadLayout = 0;
            bool threadRead = false;
            try
            {
                focused = FocusedGuiThread(tid);
                if (focused != 0)
                {
                    threadLayout = LayoutIdOf(focused);
                    threadRead = threadLayout != 0;
                }
            }
            catch { }

            string layoutTxt = windowLayout == 0 ? "读不到布局" : (LayoutName(windowLayout) + "布局");

            // 证据一：布局不是中日韩 → 一定不截键
            if ((windowLayout != 0 && !IsCjkLayout(windowLayout)) || (threadRead && !IsCjkLayout(threadLayout)))
            {
                detail = layoutTxt + "，非中日韩，按键直达";
                return ImeDirect;
            }

            int wndOpen; uint wndConv;
            bool wndOk = ReadImeWindowState(h, out wndOpen, out wndConv);
            int open = ReadOpenStatus(h);
            bool? alnum = open == 1 ? ReadAlphanumeric(h) : (bool?)null;

            if (wndOk)
            {
                if (wndOpen == 0)
                {
                    detail = layoutTxt + "，输入法已关闭，按键直达";
                    return ImeDirect;
                }
                if (IsAlphanumeric(wndConv))
                {
                    detail = layoutTxt + "，英文/半角输入状态，按键直达";
                    return ImeDirect;
                }
                detail = layoutTxt + "，输入法在中文输入状态（转换模式 0x" + wndConv.ToString("X4")
                    + "），请按 Shift 切英文";
                return ImeNative;
            }

            if (open == 0 || alnum == true)
            {
                detail = layoutTxt + "，英文输入状态，按键直达";
                return ImeDirect;
            }
            if (open == 1 && alnum == false)
            {
                detail = layoutTxt + "，输入法在中文输入状态，请按 Shift 切英文";
                return ImeNative;
            }
            if (windowLayout == 0)
            {
                detail = "读不到键盘布局与输入法状态（若收不到按键，请按 Shift 切英文）";
                return ImeUnknown;
            }
            detail = layoutTxt + "，读不准输入法状态（若收不到按键，请按 Shift 切英文）";
            return ImeUnknown;
        }

        public static Report Run()
        {
            Report r = new Report();
            bool admin = IsSelfElevated();
            r.Admin = new Item("管理员", admin ? Pass : Warn,
                admin ? "已提权" : "未提权（若目标程序以管理员运行，模拟按键会被系统拦截）");

            string detail;
            int mode = DetectImeMode(out detail);
            if (mode == ImeDirect)
                r.Ime = new Item("输入法", Pass, detail);
            else if (mode == ImeNative)
                r.Ime = new Item("输入法", Fail, detail);
            else
                r.Ime = new Item("输入法", Warn, detail);
            return r;
        }
    }

    /// <summary>
    /// 用户设置持久化（对应原版 v2.0.1 修的“设置从未保存”）。
    /// 写在 exe 同目录的 设置.ini（key=value 文本）：不写注册表、不散落到其他目录、
    /// 不依赖反射式 JSON（避免裁剪/权限导致静默失败）。任何读写失败都不影响软件运行。
    /// </summary>
    public static class Settings
    {
        static string PathFile
        {
            get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "设置.ini"); }
        }

        static Dictionary<string, string> map = new Dictionary<string, string>();
        static bool loaded;

        public static void Load()
        {
            map.Clear();
            loaded = true;
            try
            {
                if (!File.Exists(PathFile)) return;
                string[] lines = File.ReadAllLines(PathFile, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i];
                    if (ln == null) continue;
                    ln = ln.Trim();
                    if (ln.Length == 0 || ln[0] == '#' || ln[0] == ';') continue;
                    int eq = ln.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = ln.Substring(0, eq).Trim();
                    string v = ln.Substring(eq + 1).Trim();
                    map[k] = v;
                }
            }
            catch { }
        }

        public static string GetStr(string key, string def)
        {
            if (!loaded) Load();
            string v;
            if (map.TryGetValue(key, out v)) return v;
            return def;
        }

        public static int GetInt(string key, int def)
        {
            string v = GetStr(key, null);
            int r;
            if (v != null && int.TryParse(v, out r)) return r;
            return def;
        }

        public static bool GetBool(string key, bool def)
        {
            string v = GetStr(key, null);
            if (v == null) return def;
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }

        public static void Set(string key, object val)
        {
            if (!loaded) Load();
            if (val == null) return;
            string v;
            if (val is bool) v = ((bool)val) ? "1" : "0";
            else v = Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture);
            map[key] = v;
        }

        public static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 口琴模拟器 用户设置（可直接编辑，不要改 key 名）");
                foreach (KeyValuePair<string, string> kv in map)
                    sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(PathFile, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }
    }

    /// <summary>
    /// MIDI 设备实时演奏（对应原版 v2.0.2 issue #4）。
    /// 接一个 MIDI 键盘 / 电子琴 / 软件 MIDI 源，收到 NoteOn/NoteOff 就发声；
    /// 同时把 MIDI 音高映射成口琴指法（孔位 + 鼠标层）显示在界面上。
    /// 默认关闭，避免插着 MIDI 设备时误触发。
    /// </summary>
    public static class MidiIn
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MIDIINCAPS
        {
            public ushort wMid, wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwSupport;
        }

        delegate void MidiInProc(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")] static extern uint midiInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint midiInGetDevCaps(UIntPtr dev, ref MIDIINCAPS caps, uint size);
        [DllImport("winmm.dll")] static extern uint midiInOpen(out IntPtr h, uint dev, MidiInProc cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern uint midiInClose(IntPtr h);
        [DllImport("winmm.dll")] static extern uint midiInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern uint midiInStop(IntPtr h);
        [DllImport("winmm.dll")] static extern uint midiInReset(IntPtr h);
        const uint CALLBACK_FUNCTION = 0x00030000;
        const uint MIM_DATA = 0x03C3;

        static IntPtr handle = IntPtr.Zero;
        static MidiInProc proc;                 // 必须保持引用，否则回调被 GC 回收后崩
        public static bool IsOpen { get { return handle != IntPtr.Zero; } }
        public static int LastNote = -1;
        public static int LastVelocity;
        public static int ActiveCount;
        public static string LastError = "";
        public static int OpenDeviceIndex = -1;

        /// <summary>枚举本机 MIDI 输入设备名称。</summary>
        public static string[] Devices()
        {
            List<string> list = new List<string>();
            try
            {
                uint n = midiInGetNumDevs();
                for (uint i = 0; i < n; i++)
                {
                    MIDIINCAPS caps = new MIDIINCAPS();
                    uint r = midiInGetDevCaps((UIntPtr)i, ref caps, (uint)Marshal.SizeOf(typeof(MIDIINCAPS)));
                    if (r == 0) list.Add(caps.szPname == null ? ("MIDI 设备 " + i) : caps.szPname);
                }
            }
            catch { }
            return list.ToArray();
        }

        public static bool Open(int devIndex)
        {
            Close();
            LastError = "";
            try
            {
                proc = new MidiInProc(OnMidi);
                IntPtr h;
                uint r = midiInOpen(out h, (uint)devIndex, proc, IntPtr.Zero, CALLBACK_FUNCTION);
                if (r != 0) { LastError = "midiInOpen 失败，代码 " + r; return false; }
                handle = h;
                r = midiInStart(handle);
                if (r != 0) { LastError = "midiInStart 失败，代码 " + r; Close(); return false; }
                OpenDeviceIndex = devIndex;
                return true;
            }
            catch (Exception ex) { LastError = ex.Message; Close(); return false; }
        }

        public static void Close()
        {
            try
            {
                if (handle != IntPtr.Zero)
                {
                    midiInStop(handle);
                    midiInReset(handle);
                    midiInClose(handle);
                }
            }
            catch { }
            handle = IntPtr.Zero;
            proc = null;
            OpenDeviceIndex = -1;
            LastNote = -1;
            ActiveCount = 0;
        }

        static void OnMidi(IntPtr h, uint msg, IntPtr inst, IntPtr p1, IntPtr p2)
        {
            if (msg != MIM_DATA) return;
            try
            {
                int data = (int)p1.ToInt64();
                int status = data & 0xFF;
                int d1 = (data >> 8) & 0xFF;
                int d2 = (data >> 16) & 0xFF;
                int cmd = status & 0xF0;
                if (cmd == 0x90 && d2 > 0)
                {
                    LastNote = d1;
                    LastVelocity = d2;
                    ActiveCount++;
                }
                else if (cmd == 0x80 || (cmd == 0x90 && d2 == 0))
                {
                    if (ActiveCount > 0) ActiveCount--;
                    if (LastNote == d1) LastNote = -1;
                }
            }
            catch { }
        }
    }

    public static class InputInjector
    {
        public const int INPUT_MOUSE = 0;
        public const int INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_SCANCODE = 0x0008;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const int WM_HOTKEY = 0x0312;

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public int type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        /// <summary>把 Keys 转成给人看的文字，例如 "F6"、"Ctrl + F6"。</summary>
        public static string KeyText(Keys k)
        {
            if (k == Keys.None) return "（未设置）";
            string s = "";
            if ((k & Keys.Control) == Keys.Control) s += "Ctrl + ";
            if ((k & Keys.Alt) == Keys.Alt) s += "Alt + ";
            if ((k & Keys.Shift) == Keys.Shift) s += "Shift + ";
            Keys baseKey = k & ~(Keys.Control | Keys.Alt | Keys.Shift);
            s += baseKey.ToString();
            return s;
        }

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public static readonly int[] VkKeys = { 0x5A, 0x58, 0x43, 0x56, 0x42, 0x4E, 0x4D, 0xBC };

        static bool[] heldKey = new bool[8];
        static bool heldLeft, heldMid, heldRight;
        public static bool WantLeft, WantMid, WantRight;
        public static int WantHole = -1;

        // 关键：记录"我们是不是真的往外发过按下"。
        // 只有发过，结束播放时才必须补一次释放；否则会出现键卡在按下状态一直响。
        static bool injectedKey;
        static bool injectedMouse;

        static int HoleOf(string key)
        {
            for (int i = 0; i < Util.KeyNames.Length; i++)
                if (Util.KeyNames[i] == key) return i;
            return -1;
        }

        static void Send(INPUT inp)
        {
            INPUT[] arr = new INPUT[1];
            arr[0] = inp;
            SendInput(1, arr, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void Key(int hole, bool down, bool actuallySend)
        {
            if (hole < 0 || hole > 7) return;
            if (down) WantHole = hole;
            else if (WantHole == hole)
            {
                WantHole = -1;
                for (int i = 7; i >= 0; i--) if (i != hole && heldKey[i]) { WantHole = i; break; }
            }
            if (!actuallySend) return;
            if (heldKey[hole] == down) return;
            heldKey[hole] = down;
            if (down) injectedKey = true;
            INPUT inp = new INPUT();
            inp.type = INPUT_KEYBOARD;
            inp.U.ki.wVk = 0;
            inp.U.ki.wScan = (ushort)Util.Scan[hole];
            inp.U.ki.dwFlags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP);
            Send(inp);
        }

        public static void MouseBtn(string code, bool down, bool actuallySend)
        {
            if (code == "Left") WantLeft = down;
            else if (code == "Middle") WantMid = down;
            else if (code == "Right") WantRight = down;
            if (!actuallySend) return;

            uint flags = 0;
            if (code == "Left")
            {
                if (heldLeft == down) return;
                heldLeft = down;
                flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
            }
            else if (code == "Right")
            {
                if (heldRight == down) return;
                heldRight = down;
                flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
            }
            else if (code == "Middle")
            {
                if (heldMid == down) return;
                heldMid = down;
                flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
            }
            else return;

            if (down) injectedMouse = true;
            INPUT inp = new INPUT();
            inp.type = INPUT_MOUSE;
            inp.U.mi.dwFlags = flags;
            Send(inp);
        }

        public static void Execute(MacroStep s, bool sendInput)
        {
            if (s == null) return;
            if (s.Action != null && s.Action.StartsWith("Key"))
                Key(HoleOf(s.Code), s.Action == "KeyDown", sendInput);
            else
                MouseBtn(s.Code, s.Action == "MouseDown", sendInput);
        }

        /// <summary>
        /// 释放全部按键与鼠标修饰键。
        ///
        /// 这里有个曾经导致"演奏完之后有个键一直按着不放、一直响"的坑：
        /// 旧写法是"调用方说不用真发，就什么都不发"。
        /// 但播放结束时窗口往往正在前台（GetForegroundWindow()==Handle），
        /// 调用方于是传 false，结果前面真发出去的 KeyDown 永远等不到 KeyUp。
        ///
        /// 现在的规则：只要本次真的往外发过按下，就无条件把对应的松开补发出去；
        /// 没发过才只清内部状态（避免误伤用户自己在别的窗口按住的键）。
        /// </summary>
        public static void ForceRelease(bool sendInput)
        {
            bool needKeys = sendInput || injectedKey;
            bool needMouse = sendInput || injectedMouse;

            for (int i = 0; i < 8; i++)
            {
                // 真发过按下 -> 必须真发松开；否则只清状态
                if (needKeys) { heldKey[i] = true; Key(i, false, true); }
                else Key(i, false, false);
            }
            if (needMouse)
            {
                heldLeft = heldMid = heldRight = true;
                MouseBtn("Left", false, true);
                MouseBtn("Middle", false, true);
                MouseBtn("Right", false, true);
            }
            else
            {
                MouseBtn("Left", false, false);
                MouseBtn("Middle", false, false);
                MouseBtn("Right", false, false);
            }

            heldLeft = heldMid = heldRight = false;
            WantLeft = WantMid = WantRight = false;
            WantHole = -1;
            for (int i = 0; i < 8; i++) heldKey[i] = false;
            injectedKey = false;
            injectedMouse = false;
        }
    }
}

namespace DeltaHarp
{
    /// <summary>
    /// 双缓冲面板：普通 Panel 每帧先擦背景再画内容，在 5ms 定时器下会明显闪烁。
    /// 这里开启双缓冲 + 不擦背景，彻底消除描绘闪烁（实测发现的问题）。
    /// </summary>
    public class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            UpdateStyles();
        }
    }

    public class MainForm : Form
    {
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        MidiOut midi;
        string lastFile = "";
        List<MidiNote> allNotes = new List<MidiNote>();
        List<MidiNote> song = new List<MidiNote>();
        List<ChannelInfo> channels = new List<ChannelInfo>();
        List<MacroStep> macro = new List<MacroStep>();
        ChannelInfo currentCh;
        string midiPath = "";
        string songTitle = "太阳照常升起";
        int transpose = 0;
        long totalMs;

        bool playing;
        int playIndex;
        Stopwatch clock = new Stopwatch();
        Timer timer;

        bool[] keyHeld = new bool[8];
        int liveHole = -1;
        bool liveLeft, liveMid, liveRight;
        bool simLeft, simMid, simRight;
        int simHole = -1;
        int sounding = -1;
        int attackId = 0;
        int soundedAttack = -1;
        int previewMidi = -1;
        int hoverCell = -1;
        int origOnIndex = 0;
        List<MidiNote> origActive = new List<MidiNote>();

        BufferedPanel harpPanel;
        BufferedPanel mapPanel;
        ListView list;
        ListView listParts;
        ComboBox cmbMode;
        CheckBox chkFold;
        NumericUpDown numTrans;
        TrackBar sldVol;
        TrackBar sldSpeed;
        TrackBar sldTrans;
        Label lblFile;
        Label lblNow;
        Label lblHint;
        Label lblProg;
        Label lblMixHint;
        Label lblSpeedVal;
        Label lblTransTag;
        Button btnOpen, btnPlay, btnStop, btnExport, btnAuto, btnPause, btnPreflight;
        CheckBox chkInject, chkLoop, chkHighest, chkBreath;
        CheckBox chkTrimLead, chkAutoMin, chkRoll, chkMidiIn;
        ComboBox cmbMidiDevice;      // MIDI 输入设备下拉（复刻原版 v2.0.2 实时演奏）
        Label lblMidiIn;             // 显示实时收到的 MIDI 音与对应口琴指法
        int lastMidiInNote = -1;
        BufferedPanel rollPanel;             // 卷帘视图（复刻原版 v2.0.0 卷帘：纵=DB音高、横=时间）
        int rollLeftMs;              // 卷帘左边界（毫秒）
        int rollWindowMs = 12000;    // 卷帘可见时长（默认 12 秒，鼠标滚轮缩放）
        int rollSel = -1;            // 卷帘里被选中的音符
        int rollDrag = 0;            // 0=没拖 1=整体移动 2=改左端 3=改右端
        int rollDragX, rollDragY;    // 拖动起点（控件坐标）
        int rollOrigStart, rollOrigSound, rollOrigMidi;
        bool rollEdited;             // 卷帘是否改过（改过就不要再被 Rebuild 覆盖）
        // 卷帘绘图几何，供命中测试使用
        int rollPlotL, rollPlotW, rollTop, rollPlotH, rollLo, rollHi;
        float rollRowH;
        List<string> undoStack = new List<string>();
        List<string> redoStack = new List<string>();
        int leadTrimMs;              // 被剪掉的开头空拍
        string timingDiag = "";      // 本次播放的时序诊断
        ComboBox cmbCount;
        ComboBox cmbCompat;
        Label lblCompatTag;
        Label lblSpeedTag, lblVolTag, lblCount, lblHot, lblModeTag;
        ProgressBar barProg;
        List<ChannelInfo> mixOrder = new List<ChannelInfo>();
        bool fillingParts;
        NotifyIcon tray;
        Font uiFont;
        Font titleFont;
        Font holeFont;

        bool paused;
        bool counting;
        bool injecting;
        int countSec;
        int macroIndex;
        int playElapsedLast;
        int seekBaseMs;              // 跳转基准：elapsed = seekBaseMs + 时钟增量
        // 上一帧实际画出来的状态。定时器 5ms 一次，如果无脑 Invalidate，
        // 画面会以 200Hz 反复重画，看起来就是"中间一直在闪"。
        // 只有这些值变了才重绘（实测发现的问题）。
        int lastPaintHole = -2, lastPaintLeft = -2, lastPaintMid = -2, lastPaintRight = -2;
        int lastPaintSounding = -2;
        int tickPaintCounter;
        int playIndexBeforeTick;      // 上一 tick 已经刷新到的时间轴位置
        int lastSelectedIndex = -1;   // 时间轴里上一次选中的行，避免重复重绘
        bool preflightShown;         // 本次运行是否已提醒过输入法
        // 可自定义热键（默认和原版一致：F5 后退 / F6 开始暂停 / F7 前进）
        Keys hkStart = Keys.F6;
        Keys hkBack = Keys.F5;
        Keys hkFwd = Keys.F7;
        Button btnHotkey;
        bool hotkeyRegisterWarned;
        bool harpClickHold;          // 鼠标正在点口琴孔（此时左键是点击动作，不能当作“低八度”修饰键）
        Stopwatch countClock = new Stopwatch();

        Rectangle[] holeRects = new Rectangle[8];
        Rectangle[] cellRects = new Rectangle[32];

        public MainForm()
        {
            Text = "口琴模拟器 Harmonica Simulator";
            Width = 1280;
            Height = 820;
            MinimumSize = new Size(1120, 800);
            WindowState = FormWindowState.Maximized;
            BackColor = Color.FromArgb(28, 22, 18);
            ForeColor = Color.FromArgb(240, 228, 200);
            DoubleBuffered = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            uiFont = new Font("Microsoft YaHei UI", 9f);
            titleFont = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold);
            holeFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            Font = uiFont;

            Settings.Load();

            midi = new MidiOut();
            BuildUi();

            timer = new Timer();
            timer.Interval = 5;
            timer.Tick += TimerTick;
            timer.Start();

            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += FormDragDrop;
            KeyDown += FormKeyDown;
            KeyUp += FormKeyUp;
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                try { InputInjector.ForceRelease(true); } catch { }   // 关窗也要保证不留下卡住的键
                StopPlay();
                SaveSettings();
                try { InputInjector.UnregisterHotKey(Handle, 1); InputInjector.UnregisterHotKey(Handle, 2); InputInjector.UnregisterHotKey(Handle, 3); } catch { }
                if (tray != null) tray.Visible = false;
                try { MidiIn.Close(); } catch { }
                if (midi != null) midi.Dispose();
            };
            Resize += delegate(object s, EventArgs e) { LayoutUi(); };
            Shown += delegate(object s, EventArgs e)
            {
                // 默认与原版一致（F5 后退 / F6 开始暂停 / F7 前进），
                // 但用户可以自己改，改完存进「设置.ini」下次自动生效。
                RegisterHotkeys(true);
                LayoutUi();
            };

            TryLoadDefault();
            SetupTray();
            ApplyKeepVisible();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == InputInjector.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == 1) JumpBy(-5000);
                else if (id == 2) HotToggle();   // F6：开始 / 暂停 / 继续 三态切换
                else if (id == 3) JumpBy(+5000);
                return;
            }
            if (m.Msg == 0x007B) return;
            base.WndProc(ref m);
        }

        void BuildUi()
        {
            lblFile = new Label();
            lblFile.Font = titleFont;
            lblFile.ForeColor = Color.FromArgb(255, 214, 120);
            lblFile.BackColor = Color.Transparent;
            lblFile.Text = "\u4e09\u89d2\u6d32\u53e3\u7434\u6a21\u62df\u5668";
            Controls.Add(lblFile);

            btnOpen = MkBtn("\u6253\u5f00 MIDI", 18, 48);
            btnPlay = MkBtn("\u5f00\u59cb F6", 128, 48);
            btnPause = MkBtn("\u6682\u505c F6", 238, 48);
            btnStop = MkBtn("\u505c\u6b62", 348, 48);
            btnExport = MkBtn("\u5bfc\u51fa\u4e50\u8c31/\u5b8f", 458, 48);
            btnPreflight = MkBtn("\u64ad\u653e\u524d\u81ea\u68c0", 760, 86);
            btnOpen.Click += BtnOpenClick;
            btnPlay.Click += delegate(object s, EventArgs e) { HotStart(); };
            btnPause.Click += delegate(object s, EventArgs e) { HotPause(); };
            btnStop.Click += delegate(object s, EventArgs e) { StopPlay(); };
            btnExport.Click += BtnExportClick;
            btnPreflight.Click += delegate(object s2, EventArgs e) { ShowPreflight(true); };
            btnHotkey = MkBtn("\u81ea\u5b9a\u4e49\u70ed\u952e\u2026", 880, 86);
            btnHotkey.Click += delegate(object s2, EventArgs e) { EditHotkeys(); };

            lblModeTag = MkTag("\u6a21\u5f0f", 570, 52);
            Controls.Add(lblModeTag);
            cmbMode = new ComboBox();
            cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMode.Items.Add("\u53e3\u7434\u6620\u5c04");
            cmbMode.Items.Add("\u539f\u66f2\u8bd5\u542c");
            cmbMode.SelectedIndex = 0;
            cmbMode.SelectedIndexChanged += ModeChanged;
            Controls.Add(cmbMode);

            lblMixHint = new Label();
            lblMixHint.ForeColor = Color.FromArgb(255, 196, 120);
            lblMixHint.Text = "\u5df2\u8f7d\u5165\u6587\u4ef6 \u2014\u2014 \u5355\u51fb\u4e00\u884c\u4f5c\u4e3a\u4e3b\u65cb\u5f8b\uff1b\u52fe\u9009\u300c\u5408\u300d\u53ef\u6309 1\u30012\u30013 \u4f18\u5148\u7ea7\u628a\u591a\u4e2a\u58f0\u90e8\u4e00\u8d77\u5439\u3002";
            Controls.Add(lblMixHint);

            listParts = new ListView();
            listParts.View = View.Details;
            listParts.FullRowSelect = true;
            listParts.GridLines = true;
            listParts.CheckBoxes = true;
            listParts.MultiSelect = false;
            listParts.HideSelection = false;
            listParts.BackColor = Color.FromArgb(36, 28, 22);
            listParts.ForeColor = Color.FromArgb(240, 228, 200);
            listParts.Columns.Add("\u5408", 46);
            listParts.Columns.Add("\u8f68", 50);
            listParts.Columns.Add("\u58f0\u9053", 56);
            listParts.Columns.Add("\u540d\u79f0", 170);
            listParts.Columns.Add("\u97f3\u7b26", 64);
            listParts.Columns.Add("\u97f3\u57df", 120);
            listParts.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            listParts.ItemChecked += PartsItemChecked;
            listParts.SelectedIndexChanged += PartsSelected;
            listParts.MouseClick += PartsMouseClick;
            Controls.Add(listParts);

            lblTransTag = MkTag("\u79fb\u8c03", 410, 90);
            Controls.Add(lblTransTag);
            numTrans = new NumericUpDown();
            numTrans.Minimum = -12; numTrans.Maximum = 12; numTrans.Value = 0;
            numTrans.ValueChanged += TransChanged;
            Controls.Add(numTrans);
            sldTrans = new TrackBar();
            sldTrans.Minimum = -12; sldTrans.Maximum = 12; sldTrans.TickFrequency = 2; sldTrans.Value = 0;
            sldTrans.Height = 40;
            sldTrans.ValueChanged += TransSliderChanged;
            Controls.Add(sldTrans);

            btnAuto = MkBtn("\u4e00\u952e\u79fb\u8c03", 500, 86);
            btnAuto.Width = 90;
            btnAuto.Click += delegate(object s, EventArgs e) { AutoTranspose(); };

            chkFold = MkChk("\u516b\u5ea6\u6298\u53e0(\u4fdd\u6301\u539f\u8c03)", 600, 88, 170, true);
            chkFold.CheckedChanged += delegate(object s, EventArgs e) { RebuildSong(); };

            lblSpeedTag = MkTag("\u901f\u5ea6", 780, 90);
            Controls.Add(lblSpeedTag);
            sldSpeed = new TrackBar();
            sldSpeed.Minimum = 50; sldSpeed.Maximum = 150; sldSpeed.TickFrequency = 25; sldSpeed.Value = 100;
            sldSpeed.Height = 40;
            sldSpeed.ValueChanged += delegate(object s, EventArgs e) { if (lblSpeedVal != null) lblSpeedVal.Text = sldSpeed.Value.ToString() + "%"; };
            Controls.Add(sldSpeed);
            lblSpeedVal = new Label();
            lblSpeedVal.Text = "100%";
            lblSpeedVal.ForeColor = Color.FromArgb(255, 214, 120);
            Controls.Add(lblSpeedVal);

            lblVolTag = MkTag("\u97f3\u91cf", 920, 90);
            Controls.Add(lblVolTag);
            sldVol = new TrackBar();
            sldVol.Minimum = 20; sldVol.Maximum = 127; sldVol.TickFrequency = 20; sldVol.Value = 110;
            sldVol.Height = 40;
            sldVol.ValueChanged += delegate(object s, EventArgs e) { midi.SetVolume(sldVol.Value); };
            Controls.Add(sldVol);

            chkInject = MkChk("\u952e\u9f20\u5b8f\uff08\u6a21\u62df\u9f20\u6807+\u952e\u76d8\uff09", 18, 122, 210, true);
            chkInject.CheckedChanged += InjectChanged;
            chkLoop = MkChk("\u5faa\u73af", 150, 122, 60, false);
            chkHighest = MkChk("\u540c\u523b\u53d6\u6700\u9ad8", 214, 122, 120, true);
            chkHighest.CheckedChanged += delegate(object s, EventArgs e) { RebuildSong(); };
            chkBreath = MkChk("\u547c\u5438\u4f11\u6b62", 338, 122, 100, false);
            chkBreath.CheckedChanged += delegate(object s, EventArgs e) { RebuildSong(); };

            lblCount = MkTag("\u5012\u8ba1\u65f6", 580, 124);
            Controls.Add(lblCount);
            cmbCount = new ComboBox();
            cmbCount.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCount.Items.Add("0\u79d2(\u7acb\u5373)");
            cmbCount.Items.Add("3\u79d2");
            cmbCount.Items.Add("5\u79d2");
            cmbCount.Items.Add("10\u79d2");
            cmbCount.SelectedIndex = 0;
            Controls.Add(cmbCount);

            chkTrimLead = MkChk("\u53bb\u9664\u5f00\u5934\u7a7a\u62cd", 18, 150, 160, true);
            chkTrimLead.CheckedChanged += delegate(object s, EventArgs e) { RebuildSong(); };
            // 用户要求：播放期间不要自动最小化 / 不隐藏，
            // 但也绝不允许置顶挡住其它软件。
            chkAutoMin = MkChk("\u64ad\u653e\u65f6\u4fdd\u6301\u663e\u793a(\u4e0d\u6700\u5c0f\u5316)", 190, 150, 230, true);
            chkAutoMin.CheckedChanged += delegate(object s, EventArgs e) { ApplyKeepVisible(); };
            chkRoll = MkChk("\u5377\u5e18\u89c6\u56fe", 630, 150, 100, false);
            chkRoll.CheckedChanged += delegate(object s, EventArgs e) { LayoutUi(); if (rollPanel != null) rollPanel.Invalidate(); };

            lblCompatTag = MkTag("\u8f93\u5165\u517c\u5bb9", 760, 124);
            Controls.Add(lblCompatTag);
            cmbCompat = new ComboBox();
            cmbCompat.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int pi = 0; pi < InputProfile.Names().Length; pi++)
                cmbCompat.Items.Add(InputProfile.Names()[pi]);
            cmbCompat.SelectedIndex = 1;
            cmbCompat.SelectedIndexChanged += delegate(object s, EventArgs e) { RebuildSong(); };
            Controls.Add(cmbCompat);

            // ---- MIDI 设备实时演奏（复刻原版 v2.0.2）----
            chkMidiIn = MkChk("\u5b9e\u65f6MIDI\u6f14\u594f", 700, 150, 140, false);
            chkMidiIn.CheckedChanged += MidiInChanged;
            cmbMidiDevice = new ComboBox();
            cmbMidiDevice.DropDownStyle = ComboBoxStyle.DropDownList;
            RefreshMidiDevices();
            Controls.Add(cmbMidiDevice);
            lblMidiIn = new Label();
            lblMidiIn.ForeColor = Color.FromArgb(160, 220, 255);
            lblMidiIn.Text = "";
            Controls.Add(lblMidiIn);

            lblHot = new Label();
            lblHot.ForeColor = Color.FromArgb(255, 180, 120);
            lblHot.Text = "F5 \u540e\u90005\u79d2  |  F6 \u5f00\u59cb/\u6682\u505c  |  F7 \u524d\u8fdb5\u79d2  |  \u865a\u62df\u952e\u9f20\u6709\u5c01\u53f7\u98ce\u9669";
            Controls.Add(lblHot);

            barProg = new ProgressBar();
            barProg.Minimum = 0;
            barProg.Maximum = 1000;
            barProg.Value = 0;
            barProg.Style = ProgressBarStyle.Continuous;
            barProg.Cursor = Cursors.Hand;
            // 点进度条 = 跳到那个位置（原版支持拖动进度条定位）
            barProg.MouseDown += BarProgMouseDown;
            barProg.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) BarProgMouseDown(s, e);
            };
            Controls.Add(barProg);

            harpPanel = new BufferedPanel();
            harpPanel.BackColor = Color.FromArgb(42, 32, 24);
            harpPanel.Paint += DrawHarp;
            // 第 9 条验收：用鼠标直接点软件上的 8 个琴孔也能吹，不是只能用键盘。
            harpPanel.MouseDown += HarpMouseDown;
            harpPanel.MouseUp += HarpMouseUp;
            harpPanel.MouseLeave += delegate(object s2, EventArgs e) { HarpMouseUp(s2, new MouseEventArgs(MouseButtons.Left, 1, -1, -1, 0)); };
            Controls.Add(harpPanel);

            mapPanel = new BufferedPanel();
            mapPanel.BackColor = Color.FromArgb(42, 32, 24);
            mapPanel.Paint += DrawMap;
            mapPanel.MouseDown += MapMouseDown;
            mapPanel.MouseUp += MapMouseUp;
            mapPanel.MouseMove += MapMouseMove;
            mapPanel.MouseLeave += delegate(object s, EventArgs e) { hoverCell = -1; previewMidi = -1; mapPanel.Invalidate(); RefreshSound(); };
            Controls.Add(mapPanel);

            // ---- 卷帘视图（与下方按键时间轴列表互斥显示）----
            rollPanel = new BufferedPanel();
            rollPanel.BackColor = Color.FromArgb(36, 28, 22);
            rollPanel.Paint += DrawRoll;
            rollPanel.MouseDown += RollMouseDown;
            rollPanel.MouseMove += RollMouseMove;
            rollPanel.MouseUp += RollMouseUp;
            rollPanel.MouseWheel += RollMouseWheel;
            rollPanel.TabStop = true;
            rollPanel.KeyDown += RollKeyDown;
            rollPanel.Visible = false;
            Controls.Add(rollPanel);

            list = new ListView();
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.BackColor = Color.FromArgb(36, 28, 22);
            list.ForeColor = Color.FromArgb(240, 228, 200);
            list.Columns.Add("\u5e8f\u53f7", 55);
            list.Columns.Add("\u5f00\u59cb(\u79d2)", 90);
            list.Columns.Add("\u65f6\u957fms", 80);
            list.Columns.Add("\u6309\u952e", 55);
            list.Columns.Add("\u9f20\u6807", 70);
            list.Columns.Add("\u97f3\u540d", 70);
            list.Columns.Add("MIDI", 55);
            list.Columns.Add("\u5c42", 220);
            list.Columns.Add("\u5907\u6ce8", 200);
            list.Resize += delegate(object s, EventArgs e) { ResizeListColumns(); };
            Controls.Add(list);

            lblNow = new Label();
            lblNow.ForeColor = Color.FromArgb(255, 214, 120);
            lblNow.Text = "\u5f53\u524d\uff1a\u7b49\u5f85\u6309\u952e";
            Controls.Add(lblNow);

            lblProg = new Label();
            lblProg.TextAlign = ContentAlignment.MiddleRight;
            lblProg.Text = "";
            Controls.Add(lblProg);

            lblHint = new Label();
            lblHint.ForeColor = Color.FromArgb(190, 175, 150);
            lblHint.Text = "\u52fe\u9009\u300c\u952e\u9f20\u5b8f\u300d\u540e\u64ad\u653e\u4f1a\u6a21\u62df\u771f\u5b9e\u952e\u76d8+\u9f20\u6807\uff08\u50cf\u9f20\u6807\u5b8f\uff09\uff1b\u5173\u6389\u5219\u53ea\u5728\u672c\u8f6f\u4ef6\u91cc\u5439\u3002\u5012\u8ba1\u65f6\u540e\u5207\u5230\u6e38\u620f\u53ef\u76f4\u63a5\u5439\u3002\u5bfc\u51fa CSV/XML \u53ef\u5f55\u5165\u96f7\u86c7\u5b8f\u3002ZXCVBNM,";
            Controls.Add(lblHint);

            ApplySavedSettings();
            LayoutUi();
        }

        /// <summary>把上次退出时保存的设置回填到界面上。</summary>
        void ApplySavedSettings()
        {
            try
            {
                sldSpeed.Value = ClampInt(Settings.GetInt("speed", 100), sldSpeed.Minimum, sldSpeed.Maximum);
                sldVol.Value = ClampInt(Settings.GetInt("volume", 110), sldVol.Minimum, sldVol.Maximum);
                sldTrans.Value = ClampInt(Settings.GetInt("transpose", 0), sldTrans.Minimum, sldTrans.Maximum);
                cmbCount.SelectedIndex = ClampInt(Settings.GetInt("countdown", 0), 0, cmbCount.Items.Count - 1);
                cmbCompat.SelectedIndex = ClampInt(Settings.GetInt("timing", 1), 0, cmbCompat.Items.Count - 1);

                chkInject.Checked = Settings.GetBool("inject", true);
                chkLoop.Checked = Settings.GetBool("loop", false);
                chkFold.Checked = Settings.GetBool("fold", true);
                chkHighest.Checked = Settings.GetBool("highest", true);
                chkBreath.Checked = Settings.GetBool("breath", false);
                chkTrimLead.Checked = Settings.GetBool("trimLead", true);
                chkAutoMin.Checked = Settings.GetBool("keepVisible", true);

                // 自定义热键（存的是 Keys 的数值；没设过就用原版默认值）
                hkStart = LoadHotkey("hotkeyStart", Keys.F6);
                hkBack  = LoadHotkey("hotkeyBack",  Keys.F5);
                hkFwd   = LoadHotkey("hotkeyFwd",   Keys.F7);
                UpdateHotkeyLabels();

                if (lblSpeedVal != null) lblSpeedVal.Text = sldSpeed.Value.ToString() + "%";
            }
            catch { }
        }

        static int ClampInt(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>退出时保存全部用户设置。</summary>
        void SaveSettings()
        {
            try
            {
                Settings.Set("speed", sldSpeed.Value);
                Settings.Set("volume", sldVol.Value);
                Settings.Set("transpose", (int)numTrans.Value);
                Settings.Set("countdown", cmbCount.SelectedIndex);
                Settings.Set("timing", cmbCompat.SelectedIndex);
                Settings.Set("inject", chkInject.Checked);
                Settings.Set("loop", chkLoop.Checked);
                Settings.Set("fold", chkFold.Checked);
                Settings.Set("highest", chkHighest.Checked);
                Settings.Set("breath", chkBreath.Checked);
                Settings.Set("trimLead", chkTrimLead.Checked);
                Settings.Set("keepVisible", chkAutoMin.Checked);
                Settings.Set("hotkeyStart", (int)hkStart);
                Settings.Set("hotkeyBack", (int)hkBack);
                Settings.Set("hotkeyFwd", (int)hkFwd);
                Settings.Save();
            }
            catch { }
        }

        Button MkBtn(string text, int x, int y)
        {
            Button b = new Button();
            b.Text = text;
            b.Left = x; b.Top = y; b.Width = 100; b.Height = 30;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(90, 62, 32);
            b.ForeColor = Color.FromArgb(255, 230, 190);
            b.TabStop = false;
            Controls.Add(b);
            return b;
        }

        Label MkTag(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text; l.Left = x; l.Top = y; l.Width = 40; l.Height = 22;
            l.ForeColor = Color.FromArgb(200, 180, 140);
            l.BackColor = Color.Transparent;
            return l;
        }

        CheckBox MkChk(string text, int x, int y, int w, bool on)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Left = x; c.Top = y; c.Width = w; c.Height = 24;
            c.ForeColor = Color.FromArgb(255, 196, 120);
            c.Checked = on;
            Controls.Add(c);
            return c;
        }

        void LayoutUi()
        {
            if (lblFile == null || ClientSize.Width < 200) return;
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int m = 16;
            int innerW = w - m * 2;

            // 严格流式布局：每行按实际高度推进 y，任何窗口尺寸下都不会互相压住。
            int y = 8;
            lblFile.SetBounds(m, y, Math.Max(200, innerW), 28);
            y += 32;

            // ---- 第 1 行：文件 / 播放 / 模式 ----
            btnOpen.SetBounds(m + 0, y, 100, 30);
            btnPlay.SetBounds(m + 108, y, 100, 30);
            btnPause.SetBounds(m + 216, y, 100, 30);
            btnStop.SetBounds(m + 324, y, 90, 30);
            btnExport.SetBounds(m + 422, y, 118, 30);
            bool roomForMode = innerW >= 800;
            if (roomForMode)
            {
                if (lblModeTag != null) lblModeTag.SetBounds(m + 552, y + 4, 40, 22);
                if (cmbMode != null) cmbMode.SetBounds(m + 592, y + 3, 140, 24);
            }
            y += 34;

            // ---- 第 2 行：移调 / 八度折叠 / 速度 / 音量 ----
            // 需要的横向空间约 1030px；不够就把「速度/音量」折到下一行。
            bool row2Wide = innerW >= 1040;
            // 标签宽 46（够放 2 个汉字），滑条从标签右侧 +4 起，杜绝压字。
            if (lblTransTag != null) lblTransTag.SetBounds(m, y + 8, 46, 22);
            if (sldTrans != null) sldTrans.SetBounds(m + 52, y, 120, 40);
            numTrans.SetBounds(m + 176, y + 8, 46, 24);
            btnAuto.SetBounds(m + 228, y + 6, 90, 28);
            chkFold.SetBounds(m + 326, y + 8, 170, 24);
            if (row2Wide)
            {
                if (lblSpeedTag != null) lblSpeedTag.SetBounds(m + 506, y + 8, 46, 22);
                if (sldSpeed != null) sldSpeed.SetBounds(m + 558, y, 130, 40);
                if (lblSpeedVal != null) lblSpeedVal.SetBounds(m + 696, y + 8, 48, 22);
                if (lblVolTag != null) lblVolTag.SetBounds(m + 752, y + 8, 46, 22);
                if (sldVol != null) sldVol.SetBounds(m + 804, y, Math.Max(110, Math.Min(190, w - m - 804)), 40);
                y += 44;
            }
            else
            {
                y += 44;
                if (lblSpeedTag != null) lblSpeedTag.SetBounds(m, y + 8, 46, 22);
                if (sldSpeed != null) sldSpeed.SetBounds(m + 52, y, 130, 40);
                if (lblSpeedVal != null) lblSpeedVal.SetBounds(m + 190, y + 8, 48, 22);
                if (lblVolTag != null) lblVolTag.SetBounds(m + 246, y + 8, 46, 22);
                if (sldVol != null) sldVol.SetBounds(m + 298, y, Math.Max(110, Math.Min(190, w - m - 298)), 40);
                y += 44;
            }

            // ---- 第 3 行：开关组 ----
            chkInject.SetBounds(m, y, 210, 24);
            chkLoop.SetBounds(m + 214, y, 56, 24);
            chkHighest.SetBounds(m + 274, y, 120, 24);
            chkBreath.SetBounds(m + 398, y, 100, 24);
            if (lblCount != null) lblCount.SetBounds(m + 508, y + 1, 52, 22);
            if (cmbCount != null) cmbCount.SetBounds(m + 564, y, 92, 24);
            bool roomForCompat = innerW >= 940;
            if (roomForCompat)
            {
                if (lblCompatTag != null) lblCompatTag.SetBounds(m + 670, y + 1, 68, 22);
                if (cmbCompat != null) cmbCompat.SetBounds(m + 742, y, Math.Min(200, Math.Max(120, w - m - 742)), 24);
                y += 30;
            }
            else
            {
                y += 30;
                if (lblCompatTag != null) lblCompatTag.SetBounds(m, y + 1, 68, 22);
                if (cmbCompat != null) cmbCompat.SetBounds(m + 72, y, Math.Min(200, Math.Max(120, w - m - 72)), 24);
                y += 30;
            }

            // ---- 第 3.5 行：去除开头空拍 / 常驻前台(置顶) ----
            if (chkTrimLead != null) chkTrimLead.SetBounds(m, y, 170, 24);
            if (chkAutoMin != null) chkAutoMin.SetBounds(m + 180, y, 230, 24);
            if (btnPreflight != null) btnPreflight.SetBounds(m + 420, y - 2, 100, 26);
            if (chkRoll != null) chkRoll.SetBounds(m + 540, y, 100, 24);
            // 窗口够宽时才放 MIDI 设备区，否则折到下一行，避免挤在一起。
            bool roomMidi = innerW >= 940;
            if (roomMidi)
            {
                if (chkMidiIn != null) chkMidiIn.SetBounds(m + 648, y, 140, 24);
                if (cmbMidiDevice != null) cmbMidiDevice.SetBounds(m + 792, y, Math.Max(140, Math.Min(260, w - m - 792)), 24);
                y += 28;
            }
            else
            {
                y += 28;
                if (chkMidiIn != null) chkMidiIn.SetBounds(m, y, 140, 24);
                if (cmbMidiDevice != null) cmbMidiDevice.SetBounds(m + 144, y, Math.Max(140, Math.Min(260, innerW - 144)), 24);
                y += 28;
            }
            if (lblMidiIn != null) { lblMidiIn.SetBounds(m, y, innerW, 20); y += 22; }

            // ---- 第 4 行：热键提示 + 自定义热键按钮 ----
            if (btnHotkey != null) btnHotkey.SetBounds(m + Math.Max(0, innerW - 108), y - 3, 108, 26);
            if (lblHot != null) lblHot.SetBounds(m, y, Math.Max(200, innerW - 116), 22);
            y += 26;

            // ---- 第 5 行：合轨提示 ----
            if (lblMixHint != null) lblMixHint.SetBounds(m, y, innerW, 20);
            y += 22;

            // ---- 声部列表 ----
            int partsH = Math.Max(78, Math.Min(118, h * 13 / 100));
            if (listParts != null) listParts.SetBounds(m, y, innerW, partsH);
            y += partsH + 8;

            // ---- 口琴 / 32 音表 ----
            // 底部实际占用：进度条 4+14 + 两行文字 22*2 + 提示 58 = 约 120。
            int bottomH = 124;
            int mid = h - y - bottomH;
            if (mid < 150) mid = 150;

            // 左右并排：严格保证 mapPanel 右边界 <= w - m，
            // 绝不允许用 Math.Max 把宽度撑大而越界（之前“右侧被挡”的根因）。
            int availW = w - m * 2;
            int mapW = Math.Max(240, Math.Min(560, availW * 34 / 100));
            int harpW = availW - mapW - m;
            if (harpW < 360)
            {
                // 窗口太窄：先保口琴，音表缩到剩下的空间
                harpW = Math.Max(320, availW - 240 - m);
                mapW = availW - harpW - m;
            }
            if (mapW < 200) mapW = 200;
            int mapX = m + harpW + m;
            int mapActualW = w - mapX - m;
            if (mapActualW < 200)
            {
                harpW = Math.Max(280, w - m * 3 - 200);
                mapX = m + harpW + m;
                mapActualW = w - mapX - m;
            }
            if (mapActualW < 120) mapActualW = 120;

            // 口琴面板高度：下限 200（保证琴孔是圆角方形而不是扁椭圆），
            // 上限 330。空间不够时优先保口琴，压缩下方时间轴列表（它本来就可以滚动）。
            int harpH = mid * 58 / 100;
            if (harpH < 200) harpH = 200;
            if (harpH > 330) harpH = 330;
            int listMinH = 86;
            if (harpH > mid - listMinH) harpH = Math.Max(150, mid - listMinH);
            if (harpH < 120) harpH = 120;

            // 卷帘模式下不显示口琴 / 32 音表，把这段空间整体让给卷帘编辑器，
            // 否则卷帘会被挤到窗口外面看不见（这是实测发现的问题）。
            bool rollMode = chkRoll != null && chkRoll.Checked;
            if (rollMode)
            {
                harpPanel.Visible = false;
                mapPanel.Visible = false;
                harpH = 0;
                y += 0;
            }
            else
            {
                harpPanel.Visible = true;
                mapPanel.Visible = true;
                harpPanel.SetBounds(m, y, harpW, harpH);
                mapPanel.SetBounds(mapX, y, mapActualW, harpH);
                y += harpH + 8;
            }

            // ---- 按键时间轴 ----
            int listH = h - y - bottomH;
            if (listH < 86) listH = 86;
            list.SetBounds(m, y, innerW, listH);
            if (rollPanel != null) rollPanel.SetBounds(m, y, innerW, listH);
            // 卷帘与按键时间轴列表占同一块位置，用复选框切换，不会互相挡也不会撞溢。
            list.Visible = !rollMode;
            if (rollPanel != null) rollPanel.Visible = rollMode;
            y = list.Bottom;

            // ---- 底部：进度条 / 当前音 / 提示 ----
            if (barProg != null) barProg.SetBounds(m, y + 4, innerW, 14);
            // 左半区预报"当前音"，右半区预告进度；两段之间留 m 的间隙，避免相接处压字。
            int halfW = (w - m) / 2;
            lblNow.SetBounds(m, y + 22, halfW - m, 22);
            lblProg.SetBounds(m + halfW, y + 22, w - m - (m + halfW), 22);
            lblHint.SetBounds(m, y + 46, innerW, 58);

            ResizeListColumns();
            ResizePartsColumns();
            if (harpPanel != null) harpPanel.Invalidate();
            if (mapPanel != null) mapPanel.Invalidate();
        }

        void SetupTray()
        {
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Application;
            tray.Text = "口琴模拟器";
            tray.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, delegate(object s, EventArgs e) { Visible = true; WindowState = FormWindowState.Maximized; Activate(); });
            menu.Items.Add("开始 / 继续", null, delegate(object s, EventArgs e) { HotStart(); });
            menu.Items.Add("暂停 / 继续", null, delegate(object s, EventArgs e) { HotPause(); });
            menu.Items.Add("停止", null, delegate(object s, EventArgs e) { StopPlay(); });
            menu.Items.Add("关于 / 查看最新版", null, delegate(object s, EventArgs e) { ShowAbout(); });
            menu.Items.Add("退出", null, delegate(object s, EventArgs e) { Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate(object s, EventArgs e) { Visible = true; Activate(); };
        }

        /// <summary>
        /// 关于：显示版本号，并提供“打开项目主页看有没有新版”。
        /// 对应原版 v1.0.8 的“启动检查更新”——但我们不联网、不后台，
        /// 只有用户主动点才会打开浏览器，符合“不要有自启动、不要占后台”的要求。
        /// </summary>
        void ShowAbout()
        {
            const string ver = "v2.0.4-plus";
            DialogResult r = MessageBox.Show(
                "口琴模拟器 Harmonica Simulator  " + ver + "\r\n\r\n"
                + "本软件完全离线运行：不联网、不自启动、不写注册表。\r\n"
                + "设置保存在软件目录的「设置.ini」。\r\n\r\n"
                + "是否现在打开项目主页，看看有没有新版本？",
                "关于本软件", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
            {
                try
                {
                    Process.Start(Program.ProjectUrl);
                }
                catch (Exception ex) { LogCrash(ex); }
            }
        }

        // Writes the full exception to %TEMP%\HarpSim-crash.txt so a user can report
        // a problem without the app popping the raw .NET crash dialog.
        internal static void LogCrash(Exception ex)
        {
            if (ex == null) return;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "HarpSim-crash.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\r\n" + ex.ToString() + "\r\n\r\n",
                    new UTF8Encoding(true));
            }
            catch { }
        }

        void SetProgress(int value, int maximum)
        {
            if (barProg == null) return;
            try
            {
                barProg.Maximum = 1000;
                int v = maximum <= 0 ? 0 : (int)((long)value * 1000 / maximum);
                if (v < 0) v = 0;
                if (v > 1000) v = 1000;
                if (barProg.Value != v) barProg.Value = v;
            }
            catch { }
        }

        int CountSeconds()
        {
            if (cmbCount == null) return 0;
            if (cmbCount.SelectedIndex == 1) return 3;
            if (cmbCount.SelectedIndex == 2) return 5;
            if (cmbCount.SelectedIndex == 3) return 10;
            return 0;
        }

        /// <summary>
        /// “保持显示”开关：只保证本窗口不会自动最小化 / 不隐藏，
        /// 但绝不设置 TopMost，不会挡住其他软件。用户明确要求：不要置顶。
        /// </summary>
        void ApplyKeepVisible()
        {
            TopMost = false;              // 确保任何时候都不置顶
            ShowInTaskbar = true;         // 任务栏可见，方便切回
        }

        bool KeepVisibleOn()
        {
            return chkAutoMin == null || chkAutoMin.Checked;
        }

        /// <summary>
        /// 保证窗口可见：若被最小化则还原，但不抢焦点、不置顶。
        /// </summary>
        void BringToFront2()
        {
            if (!Visible) Visible = true;
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            TopMost = false;
        }

        InputProfile CurrentProfile()
        {
            int idx = (cmbCompat == null) ? 1 : cmbCompat.SelectedIndex;
            return InputProfile.FromIndex(idx);
        }

        bool InjectOn()
        {
            return chkInject != null && chkInject.Checked && !OrigMode();
        }

        void BarProgMouseDown(object sender, MouseEventArgs e)
        {
            if (!playing || barProg == null || barProg.Width <= 0) return;
            int total = 0;
            if (song.Count > 0)
                total = song[song.Count - 1].StartMs + song[song.Count - 1].SoundMs;
            if (total <= 0) return;
            int ms = (int)((long)e.X * total / barProg.Width);
            SeekTo(ms);
        }

        /// <summary>相对当前播放位置前后跳转（F5 后退 / F7 前进）。</summary>
        void JumpBy(int deltaMs)
        {
            if (!playing)
            {
                if (lblNow != null) lblNow.Text = "\u5f53\u524d\u6ca1\u5728\u64ad\u653e\uff0c\u65e0\u6cd5\u8df3\u8f6c";
                return;
            }
            int cur = seekBaseMs + (int)(clock.ElapsedMilliseconds * (sldSpeed.Value / 100.0));
            SeekTo(cur + deltaMs);
        }

        void HotStart()
        {
            if (counting)
            {
                counting = false;
                BeginSong();
                return;
            }
            if (paused && playing)
            {
                paused = false;
                clock.Start();
                lblNow.Text = "已继续播放";
                return;
            }
            if (playing) return;
            StartPlay();
        }

        void HotPause()
        {
            if (counting) { StopPlay(); return; }
            if (!playing)
            {
                lblNow.Text = "当前没有在播放，无法暂停";
                return;
            }
            if (!paused)
            {
                paused = true;
                clock.Stop();
                InputInjector.ForceRelease(InputInjector.GetForegroundWindow() != Handle);
                simHole = -1;
                midi.AllOff();
                sounding = -1;
                lblNow.Text = "已暂停 —— 再按 " + InputInjector.KeyText(hkStart) + " 继续";
                harpPanel.Invalidate();
            }
            else
            {
                paused = false;
                clock.Start();
                lblNow.Text = "已继续播放";
            }
        }

        /// <summary>
        /// 开始 / 暂停 / 继续 三态切换（热键和"开始/暂停"按钮共用）。
        /// 修掉了旧版 F6 只能开始、再按无反应的问题：
        /// 旧实现里 F6 走的是 HotStart()，而它在"正在播放且未暂停"时直接 return，
        /// 根本没有暂停分支，所以第二次按 F6 什么都没发生。
        /// </summary>
        /// <summary>把"设置.ini"里的热键读回来；越界或没设过就用默认值。</summary>
        static Keys LoadHotkey(string key, Keys def)
        {
            int v = Settings.GetInt(key, (int)def);
            if (v <= 0 || v > 0xFFFFF) return def;      // 明显不合法就回退
            return (Keys)v;
        }

        /// <summary>
        /// 注册三个全局热键。id 固定：1=后退 2=开始暂停 3=前进。
        /// 注册前先全部注销，允许用户随时改键而不用重启。
        /// </summary>
        void RegisterHotkeys(bool reportFail)
        {
            try { InputInjector.UnregisterHotKey(Handle, 1); } catch { }
            try { InputInjector.UnregisterHotKey(Handle, 2); } catch { }
            try { InputInjector.UnregisterHotKey(Handle, 3); } catch { }

            string bad = "";
            bad += RegisterOne(1, hkBack, "后退 5 秒");
            bad += RegisterOne(2, hkStart, "开始 / 暂停");
            bad += RegisterOne(3, hkFwd, "前进 5 秒");

            if (bad.Length > 0 && reportFail && !hotkeyRegisterWarned)
            {
                hotkeyRegisterWarned = true;
                MessageBox.Show(
                    "下面这些热键没能注册成功（可能被别的软件占用了）：\r\n\r\n" + bad
                    + "\r\n你可以在「自定义热键」里换成其它键。",
                    "热键注册", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        string RegisterOne(int id, Keys k, string what)
        {
            if (k == Keys.None) return "";
            uint mod = 0;
            if ((k & Keys.Control) == Keys.Control) mod |= 0x0002;
            if ((k & Keys.Alt) == Keys.Alt) mod |= 0x0001;
            if ((k & Keys.Shift) == Keys.Shift) mod |= 0x0004;
            Keys baseKey = k & ~(Keys.Control | Keys.Alt | Keys.Shift);
            bool ok = false;
            try { ok = InputInjector.RegisterHotKey(Handle, id, mod, (uint)baseKey); } catch { }
            if (!ok) return "  · " + what + "：" + InputInjector.KeyText(k) + "\r\n";
            return "";
        }

        /// <summary>把当前热键同步到按钮文字和提示行。</summary>
        void UpdateHotkeyLabels()
        {
            string s = InputInjector.KeyText(hkStart);
            string b = InputInjector.KeyText(hkBack);
            string f = InputInjector.KeyText(hkFwd);
            if (btnPlay != null) btnPlay.Text = "开始 " + s;
            if (btnPause != null) btnPause.Text = "暂停 " + s;
            if (lblHot != null)
                lblHot.Text = b + " 后退5秒  |  " + s + " 开始/暂停  |  " + f + " 前进5秒  |  虚拟键鼠有封号风险";
        }

        /// <summary>打开"自定义热键"窗口；保存后立刻生效并写入设置。</summary>
        void EditHotkeys()
        {
            Keys oldS = hkStart, oldB = hkBack, oldF = hkFwd;
            using (HotkeyDialog dlg = new HotkeyDialog(hkStart, hkBack, hkFwd))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                // 三个键不能重复，否则后面的会覆盖前面的，注册必有一个失败
                if (dlg.KStart == dlg.KBack || dlg.KStart == dlg.KFwd || dlg.KBack == dlg.KFwd)
                {
                    MessageBox.Show("三个热键不能设成同一个键，请换一个。",
                        "自定义热键", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                hkStart = dlg.KStart; hkBack = dlg.KBack; hkFwd = dlg.KFwd;
                UpdateHotkeyLabels();
                hotkeyRegisterWarned = false;      // 换了键就允许再提醒一次
                RegisterHotkeys(true);

                Settings.Set("hotkeyStart", (int)hkStart);
                Settings.Set("hotkeyBack", (int)hkBack);
                Settings.Set("hotkeyFwd", (int)hkFwd);
                Settings.Save();

                if (hkStart != oldS || hkBack != oldB || hkFwd != oldF)
                    lblNow.Text = "热键已更新：" + InputInjector.KeyText(hkStart) + " 开始/暂停";
            }
        }

        void HotToggle()
        {
            if (counting)                       // 倒计时中：按一下立即开始，不再等
            {
                counting = false;
                BeginSong();
                return;
            }
            if (playing && paused)              // 已暂停：继续
            {
                paused = false;
                clock.Start();
                if (lblNow != null) lblNow.Text = "已继续播放";
                return;
            }
            if (playing)                        // 正在播放：暂停
            {
                HotPause();
                return;
            }
            StartPlay();                        // 没在播放：开始
        }

        void AutoTranspose()
        {
            if (channels.Count == 0) return;
            ChannelInfo[] parts = SelectedParts();
            bool fold = chkFold != null && chkFold.Checked;
            bool highest = chkHighest == null || chkHighest.Checked;
            int breath = (chkBreath != null && chkBreath.Checked) ? 40 : 0;
            int bestT = 0, bestMiss = 99999, bestPlay = -1;
            for (int t = -12; t <= 12; t++)
            {
                List<MidiNote> tmp = SongBuilder.Build(allNotes, parts, t, fold, highest, breath);
                int miss = 0, play = 0;
                for (int i = 0; i < tmp.Count; i++)
                {
                    if (tmp[i].Unplayable) miss++; else play++;
                }
                if (miss < bestMiss || (miss == bestMiss && play > bestPlay) || (miss == bestMiss && play == bestPlay && Math.Abs(t) < Math.Abs(bestT)))
                {
                    bestMiss = miss; bestPlay = play; bestT = t;
                }
            }
            numTrans.Value = bestT;
            MessageBox.Show("一键移调 = " + bestT.ToString() + "  可奏 " + bestPlay.ToString() + "  缺音 " + bestMiss.ToString(), "一键移调");
        }


        static int PartKey(ChannelInfo ci)
        {
            if (ci == null) return -1;
            return (ci.Track << 8) | ci.Ch;
        }

        ChannelInfo[] SelectedParts()
        {
            if (mixOrder == null) mixOrder = new List<ChannelInfo>();
            if (channels == null) channels = new List<ChannelInfo>();
            try
            {
                List<ChannelInfo> list = new List<ChannelInfo>();
                Dictionary<int, bool> seen = new Dictionary<int, bool>();
                for (int i = 0; i < mixOrder.Count; i++)
                {
                    ChannelInfo ci = mixOrder[i];
                    if (ci == null) continue;
                    int k = PartKey(ci);
                    if (seen.ContainsKey(k)) continue;
                    list.Add(ci);
                    seen[k] = true;
                }
                if (listParts != null)
                {
                    for (int i = 0; i < listParts.Items.Count; i++)
                    {
                        ListViewItem it = listParts.Items[i];
                        if (it == null || !it.Checked) continue;
                        ChannelInfo ci = it.Tag as ChannelInfo;
                        if (ci == null) continue;
                        int k = PartKey(ci);
                        if (seen.ContainsKey(k)) continue;
                        list.Add(ci);
                        seen[k] = true;
                    }
                }
                if (list.Count == 0 && channels.Count > 0 && channels[0] != null) list.Add(channels[0]);
                return list.ToArray();
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "HarpSim-selectedparts.txt"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\r\n" + ex.ToString() +
                        "\r\nmixOrderNull=" + (mixOrder == null).ToString() +
                        "; channelsNull=" + (channels == null).ToString() +
                        "; listPartsNull=" + (listParts == null).ToString() +
                        "; mixOrderCount=" + (mixOrder == null ? -1 : mixOrder.Count).ToString() +
                        "; channelsCount=" + (channels == null ? -1 : channels.Count).ToString() +
                        "; itemCount=" + (listParts == null ? -1 : listParts.Items.Count).ToString() + "\r\n\r\n");
                }
                catch { }
                return new ChannelInfo[0];
            }
        }

        void FillParts(bool selectBest)
        {
            if (listParts == null) return;
            fillingParts = true;
            listParts.BeginUpdate();
            listParts.Items.Clear();
            mixOrder.Clear();
            for (int i = 0; i < channels.Count; i++)
            {
                ChannelInfo c = channels[i];
                ListViewItem it = new ListViewItem("");
                it.SubItems.Add(c.Track > 0 ? c.Track.ToString() : "-");
                it.SubItems.Add((c.Ch + 1).ToString());
                string name = c.Name;
                if (name == null || name.Length == 0) name = c.Role;
                if (name == null || name.Length == 0) name = "\u58f0\u9053 " + (c.Ch + 1).ToString();
                it.SubItems.Add(name);
                it.SubItems.Add(c.Count.ToString());
                it.SubItems.Add(Util.NoteName(c.MinMidi) + "-" + Util.NoteName(c.MaxMidi));
                it.Tag = c;
                listParts.Items.Add(it);
            }
            if (selectBest && listParts.Items.Count > 0)
            {
                listParts.Items[0].Checked = true;
                listParts.Items[0].Selected = true;
                mixOrder.Add(channels[0]);
            }
            listParts.EndUpdate();
            fillingParts = false;
            RefreshPartNumbers();
            UpdateMixHint();
        }

        void PartsItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (fillingParts) return;
            ChannelInfo ci = e.Item.Tag as ChannelInfo;
            if (ci == null) return;
            int k = PartKey(ci);
            if (e.Item.Checked)
            {
                bool found = false;
                for (int i = 0; i < mixOrder.Count; i++)
                    if (PartKey(mixOrder[i]) == k) { found = true; break; }
                if (!found) mixOrder.Add(ci);
            }
            else
            {
                for (int i = mixOrder.Count - 1; i >= 0; i--)
                    if (PartKey(mixOrder[i]) == k) mixOrder.RemoveAt(i);
            }
            RefreshPartNumbers();
            RebuildSong();
        }

        void PartsSelected(object sender, EventArgs e)
        {
            if (fillingParts) return;
            UpdateMixHint();
        }

        void PartsMouseClick(object sender, MouseEventArgs e)
        {
            if (fillingParts || listParts == null) return;
            ListViewHitTestInfo hit = listParts.HitTest(e.X, e.Y);
            if (hit.Item == null) return;
            if ((hit.Location & ListViewHitTestLocations.StateImage) != 0) return;
            ChannelInfo ci = hit.Item.Tag as ChannelInfo;
            if (ci == null) return;
            fillingParts = true;
            if (!hit.Item.Checked) hit.Item.Checked = true;
            fillingParts = false;
            int k = PartKey(ci);
            for (int i = mixOrder.Count - 1; i >= 0; i--)
                if (PartKey(mixOrder[i]) == k) mixOrder.RemoveAt(i);
            mixOrder.Insert(0, ci);
            RefreshPartNumbers();
            RebuildSong();
        }

        /// <summary>枚举 MIDI 输入设备到下拉框（复刻原版 v2.0.2）。</summary>
        void RefreshMidiDevices()
        {
            if (cmbMidiDevice == null) return;
            try
            {
                cmbMidiDevice.Items.Clear();
                string[] devs = MidiIn.Devices();
                if (devs.Length == 0)
                {
                    cmbMidiDevice.Items.Add("\u672a\u627e\u5230 MIDI \u8f93\u5165\u8bbe\u5907");
                    cmbMidiDevice.SelectedIndex = 0;
                    cmbMidiDevice.Enabled = false;
                    if (chkMidiIn != null) chkMidiIn.Enabled = false;
                }
                else
                {
                    for (int i = 0; i < devs.Length; i++) cmbMidiDevice.Items.Add(devs[i]);
                    cmbMidiDevice.SelectedIndex = 0;
                    cmbMidiDevice.Enabled = true;
                    if (chkMidiIn != null) chkMidiIn.Enabled = true;
                }
            }
            catch { }
        }

        /// <summary>开关实时 MIDI 演奏：开了才打开设备，关了立即释放。</summary>
        void MidiInChanged(object sender, EventArgs e)
        {
            if (chkMidiIn == null) return;
            try
            {
                if (chkMidiIn.Checked)
                {
                    int idx = cmbMidiDevice == null ? -1 : cmbMidiDevice.SelectedIndex;
                    if (idx < 0) { chkMidiIn.Checked = false; return; }
                    if (!MidiIn.Open(idx))
                    {
                        chkMidiIn.Checked = false;
                        MessageBox.Show("\u6253\u5f00 MIDI \u8bbe\u5907\u5931\u8d25\uff1a" + MidiIn.LastError, "\u5b9e\u65f6 MIDI");
                        return;
                    }
                    if (lblMidiIn != null) lblMidiIn.Text = "\u5b9e\u65f6 MIDI \u5df2\u5f00\uff1a\u8bf7\u5f39\u594f\u8bbe\u5907\uff08\u97f3\u9ad8\u4f1a\u81ea\u52a8\u6620\u5c04\u5230\u53e3\u7434\u6307\u6cd5\uff09";
                }
                else
                {
                    MidiIn.Close();
                    if (midi != null) midi.NoteOff();
                    sounding = -1;
                    if (lblMidiIn != null) lblMidiIn.Text = "\u5b9e\u65f6 MIDI \u5df2\u5173\u95ed";
                }
            }
            catch { }
        }

        /// <summary>
        /// 每个时钟 tick 轮询 MIDI 输入：收到 NoteOn 就发声并把音高映射成口琴指法，
        /// NoteOff 则停声。这是“用 MIDI 键盘直接吹本软件”的通道。
        /// </summary>
        void PollMidiIn()
        {
            if (!MidiIn.IsOpen) return;
            try
            {
                int note = MidiIn.LastNote;
                if (note == lastMidiInNote) return;
                lastMidiInNote = note;

                if (note < 0)
                {
                    if (midi != null) midi.NoteOff();
                    sounding = -1;
                    if (lblMidiIn != null)
                        lblMidiIn.Text = "\u5b9e\u65f6 MIDI \u5df2\u5f00\uff1a\u7b49\u5f85\u5f39\u594f";
                    return;
                }

                Finger f = Util.Map(note);
                if (f == null)
                {
                    if (lblMidiIn != null)
                        lblMidiIn.Text = "\u5b9e\u65f6 MIDI\uff1a" + Util.NoteName(note) + " \u8d85\u51fa\u53e3\u7434\u97f3\u57df\uff08\u5df2\u8df3\u8fc7\uff09";
                    return;
                }
                if (midi != null) { if (midi.PianoMode) midi.SetHarp(); midi.NoteOn(f.Midi, 100); }
                sounding = f.Midi;
                simHole = f.Hole;
                simLeft = f.Left; simMid = f.Middle; simRight = f.Right;
                if (lblMidiIn != null)
                    lblMidiIn.Text = "\u5b9e\u65f6 MIDI\uff1a" + Util.NoteName(note) + "  ->  \u5b54 " + (f.Hole + 1)
                        + "  \u952e " + f.Key + "  \u9f20\u6807 " + Util.MouseShort(f.Left, f.Middle, f.Right)
                        + "  " + Util.LayerText(f.Left, f.Middle, f.Right);
                if (harpPanel != null) harpPanel.Invalidate();
            }
            catch { }
        }

        void InjectChanged(object sender, EventArgs e)
        {
            if (chkInject == null) return;
            if (playing && injecting && !chkInject.Checked)
            {
                bool send = InputInjector.GetForegroundWindow() != Handle;
                InputInjector.ForceRelease(send);
                injecting = false;
                if (lblNow != null) lblNow.Text = "\u5df2\u5173\u95ed\u952e\u9f20\u5b8f\uff0c\u6539\u4e3a\u53ea\u5728\u672c\u8f6f\u4ef6\u6f14\u594f";
            }
            else if (playing && !injecting && chkInject.Checked && !OrigMode())
            {
                // Turning the macro ON mid-song must take effect immediately: jump the
                // macro cursor to the current position so we do not replay the intro.
                injecting = true;
                int elapsed = seekBaseMs + (int)(clock.ElapsedMilliseconds * (sldSpeed.Value / 100.0));
                macroIndex = 0;
                while (macroIndex < macro.Count && macro[macroIndex].AbsMs < elapsed) macroIndex++;
                simHole = -1; simLeft = simMid = simRight = false;
                InputInjector.ForceRelease(false);
                if (lblNow != null) lblNow.Text = "\u5df2\u5f00\u542f\u952e\u9f20\u5b8f\uff08\u6a21\u62df\u771f\u5b9e\u952e\u76d8+\u9f20\u6807\uff09";
            }
        }

        void RefreshPartNumbers()
        {
            if (listParts == null) return;
            try
            {
                ChannelInfo[] parts = SelectedParts();
                Dictionary<int, int> num = new Dictionary<int, int>();
                for (int i = 0; i < parts.Length; i++)
                {
                    int k = PartKey(parts[i]);
                    if (!num.ContainsKey(k)) num[k] = i + 1;
                }
                fillingParts = true;
                for (int i = 0; i < listParts.Items.Count; i++)
                {
                    ListViewItem it = listParts.Items[i];
                    if (it == null) continue;
                    ChannelInfo ci = it.Tag as ChannelInfo;
                    if (ci == null) continue;
                    int k = PartKey(ci);
                    it.Text = num.ContainsKey(k) ? num[k].ToString() : "";
                    bool should = num.ContainsKey(k);
                    if (it.Checked != should) it.Checked = should;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "HarpSim-refreshparts.txt"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\r\n" + ex.ToString() +
                        "\r\nlistPartsNull=" + (listParts == null).ToString() +
                        "; itemsNull=" + (listParts.Items == null).ToString() +
                        "; itemCount=" + (listParts.Items == null ? -1 : listParts.Items.Count).ToString() +
                        "; fillingParts=" + fillingParts.ToString() + "\r\n\r\n");
                }
                catch { }
            }
            finally
            {
                fillingParts = false;
            }
        }

        void UpdateMixHint()
        {
            if (lblMixHint == null) return;
            ChannelInfo[] parts = SelectedParts();
            if (parts.Length == 0)
            {
                lblMixHint.Text = "\u6253\u5f00 MIDI \u6587\u4ef6\uff0c\u5e76\u5728\u5de6\u4fa7\u5355\u51fb\u4e00\u884c\u4f5c\u4e3a\u4e3b\u65cb\u5f8b\uff1b\u52fe\u9009\u300c\u5408\u300d\u53ef\u628a\u591a\u4e2a\u58f0\u90e8\u4e00\u8d77\u5439\u3002";
                return;
            }
            ChannelInfo m = parts[0];
            string name = m.Name;
            if (name == null || name.Length == 0) name = m.Role;
            if (name == null) name = "";
            if (parts.Length == 1)
            {
                lblMixHint.Text = "\u4e3b\u65cb\u5f8b\uff1a\u8f68\u9053 " + m.Track.ToString() + " / \u58f0\u9053 " + (m.Ch + 1).ToString()
                    + "\uff08\u5171 " + m.Count.ToString() + " \u97f3\uff09 " + name
                    + "    \u52fe\u9009\u300c\u5408\u300d\u53ef\u628a\u591a\u4e2a\u58f0\u90e8\u4e00\u8d77\u5439";
            }
            else
            {
                string nums = "";
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) nums += ",";
                    nums += (i + 1).ToString();
                }
                lblMixHint.Text = "\u5408\u594f " + parts.Length.ToString() + " \u4e2a\u58f0\u90e8\uff08\u4f18\u5148\u7ea7 " + nums
                    + "\uff09\uff1a\u51b2\u7a81\u65f6\u5148\u5439\u7f16\u53f7\u5c0f\u7684    \u4e3b\u65cb\u5f8b "
                    + name + " " + m.Count.ToString() + "\u97f3";
            }
        }

        string MixLabel(ChannelInfo[] parts)
        {
            if (parts == null || parts.Length == 0) return "";
            if (parts.Length == 1)
                return "\u58f0\u9053" + (parts[0].Ch + 1).ToString() + " " + (parts[0].Role == null ? "" : parts[0].Role);
            return "\u5408\u594f" + parts.Length.ToString() + "\u58f0\u90e8";
        }

        void TransChanged(object sender, EventArgs e)
        {
            transpose = (int)numTrans.Value;
            if (sldTrans != null && sldTrans.Value != transpose)
                sldTrans.Value = transpose;
            RebuildSong();
        }

        void TransSliderChanged(object sender, EventArgs e)
        {
            if (numTrans != null && (int)numTrans.Value != sldTrans.Value)
                numTrans.Value = sldTrans.Value;
        }

        void ResizePartsColumns()
        {
            if (listParts == null || listParts.Columns.Count < 6) return;
            int used = 0;
            int[] wids = new int[] { 46, 50, 56, 0, 64, 120 };
            for (int i = 0; i < wids.Length; i++)
            {
                if (i == 3) continue;
                used += wids[i];
                listParts.Columns[i].Width = wids[i];
            }
            listParts.Columns[3].Width = Math.Max(120, listParts.ClientSize.Width - used - 24);
        }

        void ResizeListColumns()
        {
            if (list == null || list.Columns.Count == 0) return;
            int used = 0;
            for (int i = 0; i < list.Columns.Count - 1; i++) used += list.Columns[i].Width;
            list.Columns[list.Columns.Count - 1].Width = Math.Max(200, list.ClientSize.Width - used - 8);
        }

        bool OrigMode()
        {
            return cmbMode != null && cmbMode.SelectedIndex == 1;
        }

        void ModeChanged(object sender, EventArgs e)
        {
            StopPlay();
            if (OrigMode()) midi.SetPiano();
            else midi.SetHarp();
            if (channels.Count > 0) RebuildSong();
        }

        void TryLoadDefault()
        {
            // 只找软件自己目录里的示例曲，不再引用任何开发机上的绝对路径
            string[] cands = new string[] {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sun.mid"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "太阳照常升起.mid")
            };
            foreach (string p in cands)
            {
                if (File.Exists(p)) { LoadMidi(p); return; }
            }
        }

        void BtnOpenClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "MIDI / 卡拉OK 文件|*.mid;*.midi;*.kar;*.rmi|MIDI 文件|*.mid;*.midi|所有文件|*.*";
                if (dlg.ShowDialog() == DialogResult.OK) LoadMidi(dlg.FileName);
            }
        }

        void FormDragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0) LoadMidi(files[0]);
        }

        void LoadMidi(string path)
        {
            try
            {
                StopPlay();
                long tot;
                List<ChannelInfo> chs;
                allNotes = MidiParser.Parse(path, out tot, out chs);
                totalMs = tot;
                channels = chs;
                midiPath = path;
                songTitle = Path.GetFileNameWithoutExtension(path);
                if (songTitle.IndexOf("太阳") >= 0 || songTitle.ToLower().IndexOf("sun") >= 0)
                    songTitle = "太阳照常升起";
                mixOrder.Clear();
                FillParts(true);
                lblFile.Text = songTitle;
                RebuildSong();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "打开失败");
            }
        }

        void RebuildSong()
        {
            if (channels.Count == 0) return;
            // 卷帘里改过之后，参数变化不再覆盖编辑结果：
            // 想回到原始内容，重新打开一次 MIDI 即可。
            if (rollEdited)
            {
                macro = SongBuilder.MakeMacro(SongBuilder.PlayableOnly(song), CurrentProfile());
                if (lblProg != null)
                    lblProg.Text = "卷帘已编辑    音符 " + song.Count.ToString() + "    （改参数不会覆盖，重开文件可还原）";
                if (rollPanel != null) rollPanel.Invalidate();
                return;
            }
            ChannelInfo[] parts = SelectedParts();
            currentCh = parts.Length > 0 ? parts[0] : channels[0];
            bool fold = chkFold != null && chkFold.Checked;
            bool highest = chkHighest == null || chkHighest.Checked;
            int breath = (chkBreath != null && chkBreath.Checked) ? 40 : 0;
            bool trimLead = chkTrimLead == null || chkTrimLead.Checked;   // 默认去掉开头空拍
            song = SongBuilder.Build(allNotes, parts, transpose, fold, highest, breath, trimLead, out leadTrimMs);
            rollLeftMs = 0;   // 新曲子从头开始看
            rollSel = -1;
            rollEdited = false;
            macro = SongBuilder.MakeMacro(SongBuilder.PlayableOnly(song), CurrentProfile());
            FillList();
            RefreshPartNumbers();
            UpdateMixHint();
            harpPanel.Invalidate();
            int last = 0;
            int playable = 0, miss = 0;
            for (int i = 0; i < song.Count; i++)
            {
                if (song[i].Unplayable) miss++;
                else playable++;
                int end = song[i].StartMs + song[i].SoundMs;
                if (end > last) last = end;
            }
            int foldCount = 0;
            for (int fi = 0; fi < song.Count; fi++) { if (song[fi].Folded) foldCount++; }
            string foldTxt = foldCount > 0 ? ("    已折叠" + foldCount.ToString() + "音") : "";
            string mode = OrigMode() ? "原曲试听(钢琴多声部)" : "口琴映射";
            string trimTxt = leadTrimMs > 0 ? ("    已剪开头 " + (leadTrimMs / 1000.0).ToString("0.00") + "s") : "";
            lblProg.Text = mode + "    可奏 " + playable.ToString() + " / " + song.Count.ToString()
                + "    按不到 " + miss.ToString() + foldTxt + trimTxt + "    " + MixLabel(parts);
        }

        void FillList()
        {
            list.BeginUpdate();
            list.Items.Clear();
            for (int i = 0; i < song.Count; i++)
            {
                MidiNote n = song[i];
                Finger f = n.Fing;
                ListViewItem it = new ListViewItem((i + 1).ToString());
                it.SubItems.Add((n.StartMs / 1000.0).ToString("0.000"));
                it.SubItems.Add(n.SoundMs.ToString());
                if (f == null)
                {
                    it.SubItems.Add("-");
                    it.SubItems.Add("-");
                    it.SubItems.Add(Util.NoteName(n.Midi));
                    it.SubItems.Add(n.Midi.ToString());
                    it.SubItems.Add("按不到");
                    it.SubItems.Add("超出8孔原调音域，已跳过");
                    it.ForeColor = Color.FromArgb(255, 132, 110);
                }
                else
                {
                    it.SubItems.Add(f.Key);
                    it.SubItems.Add(Util.MouseShort(f.Left, f.Middle, f.Right));
                    it.SubItems.Add(f.Name);
                    it.SubItems.Add(n.Midi.ToString());
                    it.SubItems.Add(f.Layer);
                    it.SubItems.Add(n.Folded ? "已折叠(保持原调)" : "");
                }
                list.Items.Add(it);
            }
            list.EndUpdate();
        }
        void BtnExportClick(object sender, EventArgs e)
        {
            if (song.Count == 0)
            {
                MessageBox.Show("请先打开 MIDI。");
                return;
            }
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MIDI");
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Exporter.WriteAll(dir, songTitle, song, macro, currentCh, transpose);
                if (File.Exists(midiPath))
                {
                    string dst = Path.Combine(dir, Path.GetFileName(midiPath));
                    if (!string.Equals(Path.GetFullPath(dst), Path.GetFullPath(midiPath), StringComparison.OrdinalIgnoreCase))
                        File.Copy(midiPath, dst, true);
                }
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                MessageBox.Show("导出失败：" + ex.Message, "导出失败");
                return;
            }
            MessageBox.Show("已导出到：\n" + dir + "\n\n包含：乐谱.txt、macro-razer.csv、macro-razer.xml、MIDI文件", "导出完成");
        }

        void FormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                if (playing) StopPlay(); else StartPlay();
                e.Handled = true; e.SuppressKeyPress = true; return;
            }
            if (e.KeyCode == Keys.Escape) { StopPlay(); return; }
            int hole = Util.HoleFromKey(e.KeyCode);
            if (hole >= 0)
            {
                if (!keyHeld[hole])
                {
                    keyHeld[hole] = true;
                    liveHole = hole;
                    RefreshSound();
                    harpPanel.Invalidate();
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        void FormKeyUp(object sender, KeyEventArgs e)
        {
            int hole = Util.HoleFromKey(e.KeyCode);
            if (hole >= 0)
            {
                keyHeld[hole] = false;
                if (liveHole == hole)
                {
                    liveHole = -1;
                    for (int i = 7; i >= 0; i--) if (keyHeld[i]) { liveHole = i; break; }
                }
                RefreshSound();
                harpPanel.Invalidate();
            }
        }

        /// <summary>
        /// 播放前自检：管理员权限 + 输入法状态。
        /// 输入法处于中文状态时会截走字母键，这是“按了没反应”的常见原因，
        /// 所以在真正开始后台播放前强制提醒一次。
        /// </summary>
        /// <summary>手动自检：把管理员与输入法两项结果都告诉用户。</summary>
        void ShowPreflight(bool interactive)
        {
            Preflight.Report rep = Preflight.Run();
            string mark;
            if (rep.Ime.Status == Preflight.Pass) mark = "[通过]";
            else if (rep.Ime.Status == Preflight.Fail) mark = "[不通过]";
            else mark = "[提醒]";
            string crlf = "\r\n\r\n";
            string txt = "播放前自检结果" + crlf
                + (rep.Admin.Passed ? "[通过]" : "[提醒]") + " 权限：" + rep.Admin.Detail + crlf
                + mark + " 输入法：" + rep.Ime.Detail + crlf
                + "提示：中文输入法会截走字母键，播放前请按 Shift 切到英文。";
            MessageBox.Show(txt, "播放前自检");
        }

        bool CheckBeforePlay()
        {
            Preflight.Report rep = Preflight.Run();
            if (rep.Ime.Status == Preflight.Fail)
            {
                string crlf = "\r\n\r\n";
                MessageBox.Show(
                    "检测到：" + rep.Ime.Detail + crlf
                    + "请先把输入法切到英文（按 Shift 或 Ctrl+空格），"
                    + "否则目标程序或录入工具里字母键会被输入法截走。" + crlf
                    + "点“确定”后仍可继续播放。",
                    "播放前自检：输入法");
            }
            return true;
        }

        void StartPlay()
        {
            if (playing || counting) return;
            if (OrigMode())
            {
                if (allNotes.Count == 0)
                {
                    MessageBox.Show("\u8bf7\u5148\u6253\u5f00 MIDI\u3002");
                    return;
                }
            }
            else if (song.Count == 0)
            {
                MessageBox.Show("\u8bf7\u5148\u6253\u5f00 MIDI\u3002");
                return;
            }
            if (InjectOn() && !preflightShown) { preflightShown = true; CheckBeforePlay(); }
            countSec = CountSeconds();
            if (InjectOn() && countSec > 0)
            {
                counting = true;
                countClock.Reset();
                countClock.Start();
                lblNow.Text = countSec.ToString() + " \u79d2\u540e\u5f00\u59cb\u5439\u594f\u2014\u2014\u8bf7\u5207\u5230\u6e38\u620f\u5e76\u88c5\u5907\u53e3\u7434\uff0c\u6216\u7559\u5728\u672c\u7a97\u53e3\u6d4b\u8bd5";
                return;
            }
            BeginSong();
        }

        void BeginSong()
        {
            counting = false;
            paused = false;
            injecting = InjectOn();
            if (OrigMode())
            {
                midi.SetPiano();
                origOnIndex = 0;
                origActive.Clear();
                injecting = false;
            }
            else
            {
                midi.SetHarp();
            }
            playing = true;
            seekBaseMs = 0;
            playIndex = 0;
            macroIndex = 0;
            playIndexBeforeTick = 0;
            lastSelectedIndex = -1;
            simHole = -1;
            simLeft = simMid = simRight = false;
            InputInjector.WantHole = -1;
            InputInjector.WantLeft = InputInjector.WantMid = InputInjector.WantRight = false;
            clock.Reset();
            clock.Start();
            lastPaintHole = -2; lastPaintSounding = -2;
            lblNow.Text = injecting ? "\u952e\u9f20\u5b8f\u6f14\u594f\u4e2d..." : "\u64ad\u653e\u4e2d...";
            RefreshSound();
        }

        void StopPlay()
        {
            // 停止时一定要释放：不再用 injecting 当条件，
            // 因为演奏过程中可能切过窗口、注入状态会变。
            // ForceRelease 内部会判断"本次是否真的往外发过键"，
            // 发过就必须补发松开（否则会有一个键一直按着、一直响）。
            bool sendMouse = InputInjector.GetForegroundWindow() != Handle;
            InputInjector.ForceRelease(sendMouse);
            playing = false;
            paused = false;
            counting = false;
            injecting = false;
            seekBaseMs = 0;
            clock.Stop();
            countClock.Stop();
            simHole = -1;
            simLeft = simMid = simRight = false;
            origOnIndex = 0;
            origActive.Clear();
            playIndexBeforeTick = -1;
            midi.AllOff();
            sounding = -1;
            SetProgress(0, 1000);
            lastPaintHole = -2; lastPaintSounding = -2;
            // 停止后把右上的进度/统计也复位，否则会留着上一次播放的数字（看着像还在播）
            if (lblProg != null && song.Count > 0)
            {
                int pc = 0, mc = 0;
                for (int i = 0; i < song.Count; i++) { if (song[i].Unplayable) mc++; else pc++; }
                lblProg.Text = "已停止    可奏 " + pc.ToString() + " / " + song.Count.ToString()
                    + "    按不到 " + mc.ToString();
            }
            lblNow.Text = "\u5f53\u524d\uff1a\u505c\u6b62";
            if (harpPanel != null) harpPanel.Invalidate();
            if (mapPanel != null) mapPanel.Invalidate();
        }

        /// <summary>
        /// 跳转到指定时刻继续播放（对应原版 F5 后退 / F7 前进，以及拖动进度条）。
        /// 先释放已按住的键，再把播放游标与宏游标一起定位到新位置。
        /// </summary>
        void SeekTo(int ms)
        {
            if (!playing) return;
            if (ms < 0) ms = 0;

            int total = 0;
            if (song.Count > 0)
                total = song[song.Count - 1].StartMs + song[song.Count - 1].SoundMs;
            if (total <= 0) return;
            if (ms > total) ms = total;

            // 释放所有已按下的键与音，避免跳转后卡音
            if (injecting) InputInjector.ForceRelease(InputInjector.GetForegroundWindow() != Handle);
            midi.AllOff();
            simHole = -1;
            simLeft = simMid = simRight = false;
            sounding = -1;
            soundedAttack = -1;

            // 游标定位：播放游标照原曲时间，宏游标照事件绝对时刻
            playIndex = 0;
            while (playIndex < song.Count && song[playIndex].StartMs < ms) playIndex++;
            playIndexBeforeTick = playIndex;
            lastSelectedIndex = -1;
            macroIndex = 0;
            while (macroIndex < macro.Count && macro[macroIndex].AbsMs < ms) macroIndex++;
            if (macroIndex < macro.Count && injecting)
            {
                // 若跳转点正落在某个音上，让修饰键状态跟上，避免第一个音音高不对
                MacroStep s0 = macro[macroIndex];
                if (s0.Action == "MouseDown" || s0.Action == "MouseUp")
                {
                    // 找该修饰键最近一次状态
                    bool down = false;
                    for (int i = macroIndex - 1; i >= 0; i--)
                    {
                        if (macro[i].Code == s0.Code)
                        {
                            down = (macro[i].Action == "MouseDown");
                            break;
                        }
                    }
                    if (down)
                    {
                        InputInjector.MouseBtn(s0.Code, true, InputInjector.GetForegroundWindow() != Handle);
                    }
                }
            }

            // 时钟基准 + 重启
            seekBaseMs = ms;
            clock.Reset();
            if (!paused) clock.Start();

            if (list != null && list.Items.Count > 0)
            {
                int li = Math.Min(playIndex, list.Items.Count) - 1;
                if (li >= 0) { list.Items[li].Selected = true; list.EnsureVisible(li); }
            }
            SetProgress(ms, total);
            lblNow.Text = "\u5df2\u8df3\u8f6c\u5230 " + (ms / 1000.0).ToString("0.00") + " \u79d2";
            if (harpPanel != null) harpPanel.Invalidate();
        }

        void ReadLiveKeys()
        {
            int hole = -1;
            for (int i = 0; i < 8; i++)
            {
                bool on = (GetAsyncKeyState(InputInjector.VkKeys[i]) & 0x8000) != 0;
                keyHeld[i] = on;
                if (on) hole = i;
            }
            liveHole = hole;
        }

        void TimerTick(object sender, EventArgs e)
        {
            PollMidiIn();
            if (counting)
            {
                double left = countSec - countClock.Elapsed.TotalSeconds;
                if (left <= 0)
                {
                    counting = false;
                    BeginSong();
                }
                else
                {
                    int sec = (int)Math.Ceiling(left);
                    lblNow.Text = sec.ToString() + " \u79d2\u540e\u5f00\u59cb\u5439\u594f\u2014\u2014\u8bf7\u5207\u5230\u6e38\u620f\u5e76\u88c5\u5907\u53e3\u7434";
                }
                return;
            }

            bool selfFg = InputInjector.GetForegroundWindow() == Handle;
            if (!playing)
            {
                if (ContainsFocus)
                {
                    // 鼠标点孔时，左键本身就是点击动作，不能再当成“左键=低八度”修饰键，
                    // 否则用鼠标点出来的音全会被降一个八度。中键/右键修饰仍然有效。
                    liveLeft = (GetAsyncKeyState(0x01) & 0x8000) != 0 && !harpClickHold;
                    liveRight = (GetAsyncKeyState(0x02) & 0x8000) != 0;
                    liveMid = (GetAsyncKeyState(0x04) & 0x8000) != 0;
                }
                else
                {
                    liveLeft = liveRight = liveMid = false;
                }
            }

            if (playing && paused)
            {
                return;
            }

            if (playing && OrigMode())
            {
                TickOrig();
                return;
            }

            if (playing)
            {
                double scale = sldSpeed.Value / 100.0;
                int elapsed = seekBaseMs + (int)(clock.ElapsedMilliseconds * scale);

                // Advance the song cursor exactly once per tick. The simulated harp
                // state and the timeline list selection are both driven from here so
                // they can never drift apart. (The old code advanced playIndex in two
                // separate loops, which left the list frozen while playing without
                // the keyboard/mouse macro.)
                // 一次 tick 里可能有好几个音同时到期（和弦、跳转后补音）。
                // 原来每前进一个音就 Selected+EnsureVisible+Invalidate 一次，
                // 十个音就是十次滚动、十次重绘，画面会疯狂闪 —— 实测发现的闪烁根因。
                // 现在：循环里只更新状态，循环结束后统一刷新一次。
                int advancedTo = playIndex;
                while (playIndex < song.Count && song[playIndex].StartMs <= elapsed)
                {
                    MidiNote n = song[playIndex];
                    if (n.Fing != null && !n.Unplayable)
                    {
                        simHole = n.Fing.Hole;
                        simLeft = n.Fing.Left;
                        simMid = n.Fing.Middle;
                        simRight = n.Fing.Right;
                        attackId++;
                    }
                    else
                    {
                        simHole = -1;
                    }
                    playIndex++;
                    advancedTo = playIndex;
                }
                if (advancedTo != playIndexBeforeTick)
                {
                    playIndexBeforeTick = advancedTo;
                    if (list.Items.Count >= advancedTo && advancedTo > 0)
                    {
                        int li = advancedTo - 1;
                        // 原来每前进一个音就 Selected + EnsureVisible，
                        // 列表会以极高频率重绘，看起来就是一直在闪。
                        // 现在：只有当前行真的滚出可视区时才滚动并选中。
                        Rectangle itemRc = list.Items[li].Bounds;
                        bool outside = itemRc.Top < list.ClientRectangle.Top
                                    || itemRc.Bottom > list.ClientRectangle.Bottom;
                        if (outside || lastSelectedIndex != li)
                        {
                            if (lastSelectedIndex >= 0 && lastSelectedIndex < list.Items.Count)
                                list.Items[lastSelectedIndex].Selected = false;
                            list.Items[li].Selected = true;
                            if (outside) list.EnsureVisible(li);
                            lastSelectedIndex = li;
                        }
                    }
                    harpPanel.Invalidate();
                    mapPanel.Invalidate();
                }

                if (injecting)
                {
                    bool sendInput = !selfFg;
                    while (macroIndex < macro.Count && macro[macroIndex].AbsMs <= elapsed)
                    {
                        InputInjector.Execute(macro[macroIndex], sendInput);
                        macroIndex++;
                    }
                    if (sendInput)
                    {
                        ReadLiveKeys();
                        liveLeft = (GetAsyncKeyState(0x01) & 0x8000) != 0;
                        liveRight = (GetAsyncKeyState(0x02) & 0x8000) != 0;
                        liveMid = (GetAsyncKeyState(0x04) & 0x8000) != 0;
                    }
                    else
                    {
                        liveHole = InputInjector.WantHole;
                        liveLeft = InputInjector.WantLeft;
                        liveMid = InputInjector.WantMid;
                        liveRight = InputInjector.WantRight;
                        for (int i = 0; i < 8; i++) keyHeld[i] = (i == liveHole);
                    }
                    simHole = liveHole;
                    simLeft = liveLeft;
                    simMid = liveMid;
                    simRight = liveRight;
                }
                else if (simHole >= 0 && playIndex > 0)
                {
                    MidiNote cur = song[playIndex - 1];
                    if (cur.Fing == null || elapsed >= cur.StartMs + cur.SoundMs)
                    {
                        simHole = -1;
                        harpPanel.Invalidate();
                    }
                }

                int last = 0;
                if (song.Count > 0) last = song[song.Count - 1].StartMs + song[song.Count - 1].SoundMs;
                int playable = 0, miss = 0;
                for (int i = 0; i < song.Count; i++)
                {
                    if (song[i].Unplayable) miss++; else playable++;
                }
                SetProgress(elapsed, last);
                string tag = injecting ? "\u952e\u9f20\u5b8f" : "\u5185\u90e8";
                lblProg.Text = tag + "  " + (elapsed / 1000.0).ToString("0.00") + " / " + (last / 1000.0).ToString("0.00") + " \u79d2    "
                    + Math.Min(playIndex, song.Count).ToString() + "/" + song.Count.ToString()
                    + "    \u53ef\u594f" + playable.ToString() + " \u6309\u4e0d\u5230" + miss.ToString();
                if (elapsed > last + 200)
                {
                    if (chkLoop != null && chkLoop.Checked)
                    {
                        if (injecting) InputInjector.ForceRelease(!selfFg);
                        seekBaseMs = 0;
                        playIndex = 0;
                        macroIndex = 0;
                        clock.Reset();
                        clock.Start();
                        lblNow.Text = "\u5faa\u73af \u4e0b\u4e00\u904d";
                    }
                    else StopPlay();
                }
                // 只有画面内容真的变了才重绘，避免 200Hz 无谓刷新造成闪烁
                if (injecting)
                {
                    if (liveHole != lastPaintHole || liveLeft != (lastPaintLeft == 1)
                        || liveMid != (lastPaintMid == 1) || liveRight != (lastPaintRight == 1))
                    {
                        lastPaintHole = liveHole;
                        lastPaintLeft = liveLeft ? 1 : 0;
                        lastPaintMid = liveMid ? 1 : 0;
                        lastPaintRight = liveRight ? 1 : 0;
                        harpPanel.Invalidate();
                    }
                }
                else if (simHole != lastPaintHole || sounding != lastPaintSounding)
                {
                    lastPaintHole = simHole;
                    lastPaintLeft = simLeft ? 1 : 0;
                    lastPaintMid = simMid ? 1 : 0;
                    lastPaintRight = simRight ? 1 : 0;
                    lastPaintSounding = sounding;
                    harpPanel.Invalidate();
                    mapPanel.Invalidate();
                }
                // 卷帘每帧都有播放头在动，按固定节流刷新（约 20fps），不要 200fps
                if (rollPanel != null && rollPanel.Visible)
                {
                    tickPaintCounter++;
                    if (tickPaintCounter >= 12) { tickPaintCounter = 0; rollPanel.Invalidate(); }
                }
            }

            RefreshSound();
        }

        void TickOrig()
        {
            double scale = sldSpeed.Value / 100.0;
            int elapsed = seekBaseMs + (int)(clock.ElapsedMilliseconds * scale);
            while (origOnIndex < allNotes.Count && allNotes[origOnIndex].StartMs <= elapsed)
            {
                MidiNote n = allNotes[origOnIndex];
                midi.NoteOnCh(n.Ch, n.Midi, n.Vel);
                origActive.Add(n);
                origOnIndex++;
            }
            for (int i = origActive.Count - 1; i >= 0; i--)
            {
                if (origActive[i].EndMs <= elapsed)
                {
                    midi.NoteOffCh(origActive[i].Ch, origActive[i].Midi);
                    origActive.RemoveAt(i);
                }
            }
            long last = totalMs;
            if (allNotes.Count > 0)
            {
                MidiNote lastN = allNotes[allNotes.Count - 1];
                if (lastN.EndMs > last) last = lastN.EndMs;
            }
            SetProgress(elapsed, (int)last);
            lblNow.Text = "\u539f\u66f2\u8bd5\u542c\uff1aGM\u94a2\u7434 \u5168\u901a\u9053  " + origActive.Count.ToString() + " \u4e2a\u97f3\u6b63\u5728\u54cd";
            lblProg.Text = (elapsed / 1000.0).ToString("0.00") + " / " + (last / 1000.0).ToString("0.00") + " \u79d2    "
                + origOnIndex.ToString() + "/" + allNotes.Count.ToString() + " \u4e8b\u4ef6";
            if (elapsed > last + 400)
            {
                if (chkLoop != null && chkLoop.Checked)
                {
                    origOnIndex = 0;
                    origActive.Clear();
                    seekBaseMs = 0;
                    midi.AllOff();
                    clock.Reset();
                    clock.Start();
                }
                else StopPlay();
            }
        }

        void RefreshSound()
        {
            if (playing && OrigMode()) return;
            if (injecting && playing && InputInjector.GetForegroundWindow() != Handle)
            {
                if (sounding >= 0)
                {
                    midi.NoteOff();
                    sounding = -1;
                }
                return;
            }

            int hole;
            bool left, mid, right;
            int vel = 96;

            if (previewMidi >= 0)
            {
                if (sounding != previewMidi)
                {
                    if (midi.PianoMode) midi.SetHarp();
                    midi.NoteOn(previewMidi, 100);
                    sounding = previewMidi;
                    lblNow.Text = "\u8bd5\u542c\uff1a" + Util.NoteName(previewMidi);
                }
                return;
            }

            bool useLive = !playing || injecting;
            if (useLive)
            {
                hole = liveHole;
                left = liveLeft; mid = liveMid; right = liveRight;
            }
            else
            {
                hole = simHole;
                left = simLeft; mid = simMid; right = simRight;
                if (playIndex > 0 && playIndex <= song.Count) vel = song[playIndex - 1].Vel;
            }

            if (hole < 0)
            {
                if (sounding >= 0)
                {
                    midi.NoteOff();
                    sounding = -1;
                    soundedAttack = -1;
                    if (!playing) lblNow.Text = "\u5f53\u524d\uff1a\u677e\u5f00";
                }
                return;
            }

            int oct = 0;
            if (left) oct = -1;
            if (right) oct = 1;
            int midiN = Util.PitchOf(hole, oct, mid);
            if (midiN != sounding || (!useLive && playing && attackId != soundedAttack))
            {
                if (midi.PianoMode) midi.SetHarp();
                midi.NoteOn(midiN, vel);
                sounding = midiN;
                soundedAttack = attackId;
                lblNow.Text = (injecting ? "\u952e\u9f20\u5b8f " : "") + "\u5f53\u524d\uff1a" + Util.NoteName(midiN) + "    \u952e " + Util.KeyNames[hole]
                    + "    \u9f20\u6807 " + Util.MouseShort(left, mid, right) + "    " + Util.LayerText(left, mid, right);
                harpPanel.Invalidate();
                mapPanel.Invalidate();
            }
        }

        /// <summary>
        /// 鼠标点琴孔：先把坐标换成当前面板上的琴孔序号，再用现有的
        /// liveHole / keyHeld / RefreshSound 路径发声（与键盘现场吹完全同一条通路）。
        /// </summary>
        void HarpMouseDown(object sender, MouseEventArgs e)
        {
            int idx = HitHole(e.X, e.Y);
            if (idx < 0) return;
            // 点孔的这一次左键不参与八度判定（中键升半音、右键高八度仍然有效）。
            harpClickHold = true;
            if (e.Button == MouseButtons.Left) liveLeft = false;
            if (e.Button == MouseButtons.Right) liveRight = true;
            if (e.Button == MouseButtons.Middle) liveMid = true;
            if (!keyHeld[idx])
            {
                for (int i = 0; i < keyHeld.Length; i++) keyHeld[i] = false;   // 口琴是单音，同一时刻只保留最后点的孔
                keyHeld[idx] = true;
                liveHole = idx;
            }
            RefreshSound();
            harpPanel.Invalidate();
            mapPanel.Invalidate();
        }

        void HarpMouseUp(object sender, MouseEventArgs e)
        {
            // 鼠标离开或松开：释放所有被鼠标按下的琴孔
            harpClickHold = false;
            bool changed = false;
            for (int i = 0; i < keyHeld.Length; i++)
            {
                if (keyHeld[i]) { keyHeld[i] = false; changed = true; }
            }
            if (!changed) return;
            liveHole = -1;
            for (int i = 7; i >= 0; i--) if (keyHeld[i]) { liveHole = i; break; }
            RefreshSound();
            harpPanel.Invalidate();
        }

        /// <summary>命中测试：直接用 DrawHarp 算好并存下的 holeRects。</summary>
        int HitHole(int x, int y)
        {
            for (int i = 0; i < holeRects.Length; i++)
                if (holeRects[i].Width > 0 && holeRects[i].Contains(x, y)) return i;
            return -1;
        }

        /// <summary>
        /// 卷帘视图（对应原版 v2.0.0 卷帘编辑器的只读演示版）：
        /// 纵轴 = 音高（自下向上），横轴 = 时间，方块长度 = 音符时值。
        /// 播放时载入窗口自动跟随当前时刻（对应 v2.0.3 修的“试听时卷帘不跟随”）。
        /// </summary>
        void DrawRoll(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int W = rollPanel.ClientSize.Width, H = rollPanel.ClientSize.Height;
            if (W <= 10 || H <= 10) return;

            const int padL = 46, padR = 10, padT = 22, padB = 18;
            int plotW = W - padL - padR;
            int plotH = H - padT - padB;
            if (plotW < 40 || plotH < 30) return;
            rollPlotL = padL; rollPlotW = plotW; rollTop = padT; rollPlotH = plotH;

            // 音域：取当前曲子实际音域，至少保证 4 个八度可见（对应 v2.0.1）
            int lo = 127, hi = 0;
            for (int i = 0; i < song.Count; i++)
            {
                int m = song[i].Midi;
                if (m < lo) lo = m;
                if (m > hi) hi = m;
            }
            if (song.Count == 0) { lo = 60; hi = 72; }
            if (hi - lo < 48) { int mid = (lo + hi) / 2; lo = mid - 24; hi = mid + 24; }
            lo -= 1; hi += 1;
            int span = hi - lo + 1;
            rollLo = lo; rollHi = hi;

            // 时间窗口：播放中跟随当前时刻，否则用手动左边界
            int nowMs = 0;
            if (playing) nowMs = seekBaseMs + (int)(clock.ElapsedMilliseconds * (sldSpeed.Value / 100.0));
            if (playing)
            {
                int want = nowMs - rollWindowMs / 3;
                if (want < 0) want = 0;
                if (want > rollLeftMs || nowMs > rollLeftMs + rollWindowMs * 2 / 3) rollLeftMs = want;
            }
            if (rollLeftMs < 0) rollLeftMs = 0;

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(28, 22, 18)))
                g.FillRectangle(bg, padL, padT, plotW, plotH);

            // 背景横线（每个半音一格，C 音加粗）
            float rowH = (float)plotH / span;
            rollRowH = rowH;
            for (int m = lo; m <= hi; m++)
            {
                int y = padT + plotH - (int)Math.Round((m - lo + 1) * rowH);
                bool isC = (m % 12) == 0;
                using (Pen p = new Pen(isC ? Color.FromArgb(70, 62, 50) : Color.FromArgb(44, 37, 30), isC ? 1.4f : 1f))
                    g.DrawLine(p, padL, y, padL + plotW, y);
                if (isC && rowH >= 5)
                {
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(180, 160, 130)))
                        g.DrawString(Util.NoteName(m), uiFont, tb, 6, y - 7);
                }
            }

            // 音符块
            using (SolidBrush note = new SolidBrush(Color.FromArgb(255, 186, 64)))
            using (SolidBrush miss = new SolidBrush(Color.FromArgb(150, 80, 70)))
            using (SolidBrush folded = new SolidBrush(Color.FromArgb(120, 200, 255)))
            {
                for (int i = 0; i < song.Count; i++)
                {
                    MidiNote n = song[i];
                    int s0 = n.StartMs - rollLeftMs;
                    int s1 = n.StartMs + Math.Max(1, n.SoundMs) - rollLeftMs;
                    if (s1 < 0 || s0 > rollWindowMs) continue;
                    if (s0 < 0) s0 = 0;
                    if (s1 > rollWindowMs) s1 = rollWindowMs;
                    int x0 = padL + (int)((long)s0 * plotW / rollWindowMs);
                    int x1 = padL + (int)((long)s1 * plotW / rollWindowMs);
                    if (x1 - x0 < 2) x1 = x0 + 2;
                    int m = n.Midi;
                    if (m < lo) m = lo;
                    if (m > hi) m = hi;
                    int y = padT + plotH - (int)Math.Round((m - lo + 1) * rowH);
                    int bh = Math.Max(2, (int)Math.Round(rowH) - 1);
                    SolidBrush b = n.Unplayable ? miss : (n.Folded ? folded : note);
                    g.FillRectangle(b, x0, y, Math.Max(2, x1 - x0), bh);
                    if (i == rollSel)
                    {
                        // 选中的音符加白边，方便拖动改长度
                        using (Pen sp = new Pen(Color.White, 2f))
                            g.DrawRectangle(sp, x0, y, Math.Max(2, x1 - x0), bh);
                        using (SolidBrush h = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
                            g.FillRectangle(h, x0, y, Math.Max(2, x1 - x0), bh);
                    }
                }
            }

            // 播放头（红线）（对应 v2.0.3 修的跟随问题）
            if (playing)
            {
                int ph = nowMs - rollLeftMs;
                if (ph >= 0 && ph <= rollWindowMs)
                {
                    int px = padL + (int)((long)ph * plotW / rollWindowMs);
                    using (Pen p = new Pen(Color.FromArgb(255, 90, 80), 2f))
                        g.DrawLine(p, px, padT, px, padT + plotH);
                }
            }

            // 标题与时间轴
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(255, 214, 120)))
                g.DrawString("卷帘编辑器（拖动=移动音符 拉两端=改长短 Delete=删除 Ctrl+Z=撤销）  滚轮=平移 Ctrl+滚轮=缩放", uiFont, tb, padL, 3);
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(190, 175, 150)))
            {
                int secs = rollWindowMs / 1000;
                for (int t = 0; t <= secs; t += (secs > 20 ? 5 : 2))
                {
                    int x = padL + (int)((long)(t * 1000) * plotW / rollWindowMs);
                    g.DrawString(((rollLeftMs + t * 1000) / 1000) + "s", uiFont, tb, x + 2, padT + plotH + 2);
                }
            }
        }

        /// <summary>
        /// 滚轮：单独滚 = 平移，Ctrl+滚轮 = 缩放时间窗口（2秒 ~ 60 秒）。
        /// 对应原版 v1.1.0 的“卷帘缩放与编辑”交互。
        /// </summary>
        void RollMouseWheel(object sender, MouseEventArgs e)
        {
            bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;
            if (ctrl)
            {
                int old = rollWindowMs;
                if (e.Delta > 0) rollWindowMs = Math.Max(2000, rollWindowMs / 2);
                else rollWindowMs = Math.Min(60000, rollWindowMs * 2);
                // 以鼠标位置为锚点，避免缩放后跳到别处
                int plotW = Math.Max(40, rollPanel.ClientSize.Width - 56);
                int px = Math.Max(0, Math.Min(plotW, e.X - 46));
                int anchor = rollLeftMs + (int)((long)px * old / plotW);
                rollLeftMs = Math.Max(0, anchor - (int)((long)px * rollWindowMs / plotW));
            }
            else
            {
                rollLeftMs += e.Delta > 0 ? -rollWindowMs / 8 : rollWindowMs / 8;
                if (rollLeftMs < 0) rollLeftMs = 0;
            }
            rollPanel.Invalidate();
        }

        // ---------------- 卷帘编辑 ----------------

        /// <summary>把当前曲子存成一行文本，用于撤销/重做。</summary>
        string RollSnapshot()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < song.Count; i++)
            {
                MidiNote n = song[i];
                sb.Append(n.StartMs).Append(',').Append(n.SoundMs).Append(',').Append(n.Midi).Append(',')
                  .Append(n.Vel).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>用快照恢复曲子（撤销/重做）。</summary>
        void RollRestore(string snap)
        {
            if (snap == null) return;
            List<MidiNote> rebuilt = new List<MidiNote>();
            string[] items = snap.Split(';');
            for (int i = 0; i < items.Length; i++)
            {
                string it = items[i];
                if (it == null || it.Length == 0) continue;
                string[] f = it.Split(',');
                if (f.Length < 4) continue;
                int st, sd, mi, ve;
                if (!int.TryParse(f[0], out st)) continue;
                if (!int.TryParse(f[1], out sd)) continue;
                if (!int.TryParse(f[2], out mi)) continue;
                if (!int.TryParse(f[3], out ve)) continue;
                MidiNote src = null;
                for (int k = 0; k < song.Count; k++)
                    if (song[k].StartMs == st && song[k].Midi == mi) { src = song[k]; break; }
                MidiNote n = new MidiNote();
                n.StartMs = st; n.SoundMs = sd; n.Midi = mi; n.Vel = ve;
                n.EndMs = st + sd;
                if (src != null)
                {
                    n.Ch = src.Ch; n.Track = src.Track; n.Fing = src.Fing;
                    n.Folded = src.Folded; n.Unplayable = src.Unplayable;
                }
                else
                {
                    Finger f2 = Util.Map(mi);
                    n.Fing = f2;
                    n.Unplayable = (f2 == null);
                }
                rebuilt.Add(n);
            }
            song = rebuilt;
        }

        void RollPushUndo()
        {
            try
            {
                undoStack.Add(RollSnapshot());
                if (undoStack.Count > 60) undoStack.RemoveAt(0);
                redoStack.Clear();
            }
            catch { }
        }

        /// <summary>改完音符后：重算宏、刷新列表和提示。</summary>
        void RollAfterEdit()
        {
            rollEdited = true;
            try
            {
                macro = SongBuilder.MakeMacro(SongBuilder.PlayableOnly(song), CurrentProfile());
                if (list != null) FillList();
                int playable = 0, miss = 0;
                for (int i = 0; i < song.Count; i++) { if (song[i].Unplayable) miss++; else playable++; }
                if (lblProg != null)
                    lblProg.Text = "卷帘已编辑    音符 " + song.Count.ToString()
                        + "    可奏 " + playable.ToString() + "    按不到 " + miss.ToString();
            }
            catch { }
            if (rollPanel != null) rollPanel.Invalidate();
            if (harpPanel != null) harpPanel.Invalidate();
        }

        /// <summary>卷帘命中测试：返回被点中的音符序号，返回 -1 表示点空白。</summary>
        int RollHit(int x, int y)
        {
            if (rollPlotW <= 0 || rollRowH <= 0) return -1;
            if (x < rollPlotL || x > rollPlotL + rollPlotW) return -1;
            if (y < rollTop || y > rollTop + rollPlotH) return -1;
            int best = -1;
            for (int i = song.Count - 1; i >= 0; i--)
            {
                MidiNote n = song[i];
                int s0 = n.StartMs - rollLeftMs;
                int s1 = n.StartMs + Math.Max(1, n.SoundMs) - rollLeftMs;
                if (s1 < 0 || s0 > rollWindowMs) continue;
                if (s0 < 0) s0 = 0;
                if (s1 > rollWindowMs) s1 = rollWindowMs;
                int x0 = rollPlotL + (int)((long)s0 * rollPlotW / rollWindowMs);
                int x1 = rollPlotL + (int)((long)s1 * rollPlotW / rollWindowMs);
                if (x1 - x0 < 2) x1 = x0 + 2;
                int m = n.Midi;
                if (m < rollLo) m = rollLo;
                if (m > rollHi) m = rollHi;
                int yy = rollTop + rollPlotH - (int)Math.Round((m - rollLo + 1) * rollRowH);
                int bh = Math.Max(2, (int)Math.Round(rollRowH) - 1);
                Rectangle rc = new Rectangle(x0, yy, Math.Max(2, x1 - x0), bh);
                if (rc.Contains(x, y)) { best = i; break; }
            }
            return best;
        }

        /// <summary>把控件横坐标换成时间（毫秒）。</summary>
        int RollXToMs(int x)
        {
            if (rollPlotW <= 0) return 0;
            int rel = x - rollPlotL;
            if (rel < 0) rel = 0;
            if (rel > rollPlotW) rel = rollPlotW;
            return rollLeftMs + (int)((long)rel * rollWindowMs / rollPlotW);
        }

        /// <summary>把控件纵坐标换成 MIDI 音高。</summary>
        int RollYToMidi(int y)
        {
            if (rollRowH <= 0) return 60;
            int rel = (rollTop + rollPlotH) - y;
            int m = rollLo + (int)Math.Floor(rel / rollRowH);
            if (m < rollLo) m = rollLo;
            if (m > rollHi) m = rollHi;
            if (m < 0) m = 0;
            if (m > 127) m = 127;
            return m;
        }

        void RollDelete()
        {
            if (rollSel < 0 || rollSel >= song.Count) return;
            RollPushUndo();
            song.RemoveAt(rollSel);
            rollSel = -1;
            RollAfterEdit();
        }

        void RollUndo()
        {
            if (undoStack.Count == 0) return;
            string snap = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            redoStack.Add(RollSnapshot());
            RollRestore(snap);
            rollSel = -1;
            RollAfterEdit();
        }

        void RollRedo()
        {
            if (redoStack.Count == 0) return;
            string snap = redoStack[redoStack.Count - 1];
            redoStack.RemoveAt(redoStack.Count - 1);
            undoStack.Add(RollSnapshot());
            RollRestore(snap);
            rollSel = -1;
            RollAfterEdit();
        }

        /// <summary>卷帘交互：拖动=移动；拖两端=改长短；双击空白=加音；双击音符=删音。</summary>

        void RollMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle)
            {
                rollLeftMs += rollWindowMs / 4;
                rollPanel.Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                rollLeftMs -= rollWindowMs / 4;
                if (rollLeftMs < 0) rollLeftMs = 0;
                rollPanel.Invalidate();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            int hit = RollHit(e.X, e.Y);
            if (e.Clicks >= 2)
            {
                if (hit >= 0)
                {
                    // 双击已有音符 = 删除
                    RollPushUndo();
                    song.RemoveAt(hit);
                    rollSel = -1;
                    RollAfterEdit();
                }
                else
                {
                    // 双击空白 = 在鼠标位置新增一个音（默认 500ms）
                    int ms = RollXToMs(e.X);
                    int midi = RollYToMidi(e.Y);
                    RollPushUndo();
                    Finger f = Util.Map(midi);
                    MidiNote n = new MidiNote();
                    n.StartMs = Math.Max(0, ms);
                    n.SoundMs = 500;
                    n.Midi = midi;
                    n.Vel = 96;
                    n.EndMs = n.StartMs + n.SoundMs;
                    n.Fing = f;
                    n.Folded = false;
                    n.Unplayable = (f == null);
                    song.Add(n);
                    song.Sort(delegate(MidiNote a, MidiNote b) { return a.StartMs.CompareTo(b.StartMs); });
                    RollAfterEdit();
                }
                return;
            }

            rollSel = hit;
            rollDragX = e.X; rollDragY = e.Y;
            if (hit >= 0)
            {
                MidiNote n = song[hit];
                int s0 = n.StartMs - rollLeftMs;
                int s1 = n.StartMs + Math.Max(1, n.SoundMs) - rollLeftMs;
                int x0 = rollPlotL + (int)((long)s0 * rollPlotW / rollWindowMs);
                int x1 = rollPlotL + (int)((long)s1 * rollPlotW / rollWindowMs);
                if (x1 - x0 < 8) { x0 = Math.Min(x0, e.X); x1 = Math.Max(x1, e.X + 8); }
                int edge = Math.Max(4, Math.Min(10, (x1 - x0) / 4));
                if (e.X <= x0 + edge) rollDrag = 2;
                else if (e.X >= x1 - edge) rollDrag = 3;
                else rollDrag = 1;
                rollOrigStart = n.StartMs; rollOrigSound = n.SoundMs; rollOrigMidi = n.Midi;
                RollPushUndo();
            }
            else
            {
                rollDrag = 4;   // 拖空白 = 平移视图
            }
            rollPanel.Invalidate();
        }

        void RollMouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || rollDrag == 0) return;
            int dxMs = (int)((long)(e.X - rollDragX) * rollWindowMs / Math.Max(1, rollPlotW));
            if (rollDrag == 4)
            {
                rollLeftMs -= dxMs;
                if (rollLeftMs < 0) rollLeftMs = 0;
                rollDragX = e.X;
                rollPanel.Invalidate();
                return;
            }
            if (rollSel < 0 || rollSel >= song.Count) return;
            MidiNote n = song[rollSel];
            if (rollDrag == 1)
            {
                n.StartMs = Math.Max(0, rollOrigStart + dxMs);
                n.EndMs = n.StartMs + n.SoundMs;
            }
            else if (rollDrag == 2)
            {
                int newStart = Math.Max(0, rollOrigStart + dxMs);
                int newSound = rollOrigSound - (newStart - rollOrigStart);
                if (newSound < 30) { newSound = 30; newStart = rollOrigStart + rollOrigSound - 30; }
                n.StartMs = newStart;
                n.SoundMs = newSound;
                n.EndMs = newStart + newSound;
            }
            else if (rollDrag == 3)
            {
                n.SoundMs = Math.Max(30, rollOrigSound + dxMs);
                n.EndMs = n.StartMs + n.SoundMs;
            }
            rollPanel.Invalidate();
        }

        /// <summary>卷帘编辑快捷键：Delete 删除选中、Ctrl+Z 撤销、Ctrl+Y 重做。</summary>
        void RollKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) { RollDelete(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Z) { RollUndo(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Y) { RollRedo(); e.Handled = true; }
        }

        void RollMouseUp(object sender, MouseEventArgs e)
        {
            if (rollDrag == 0) return;
            bool edited = (rollDrag >= 1 && rollDrag <= 3);
            rollDrag = 0;
            if (edited)
            {
                song.Sort(delegate(MidiNote a, MidiNote b) { return a.StartMs.CompareTo(b.StartMs); });
                RollAfterEdit();
            }
        }

        void DrawHarp(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int bodyH = Math.Max(56, Math.Min(120, harpPanel.Height - 104));
            Rectangle body = new Rectangle(20, 42, Math.Max(200, harpPanel.Width - 40), bodyH);
            using (LinearGradientBrush br = new LinearGradientBrush(body, Color.FromArgb(186, 138, 62), Color.FromArgb(92, 60, 24), 90f))
                g.FillRectangle(br, body);
            using (Pen p = new Pen(Color.FromArgb(255, 214, 120), 2))
                g.DrawRectangle(p, body);

            g.DrawString("8 孔口琴  ·  ZXCVBNM,", titleFont, Brushes.Wheat, 30, 12);

            bool left = playing ? simLeft : liveLeft;
            bool mid = playing ? simMid : liveMid;
            bool right = playing ? simRight : liveRight;
            int hole = playing ? simHole : liveHole;
            int oct = 0;
            if (left) oct = -1;
            if (right) oct = 1;

            // 琴孔排布：每孔是圆角矩形。宽度按可用宽度等分，但高度不跟着面板高度
            // 无限拉伸，两者取较小者，这样不会被压成扁椭圆。
            int gap = 10;
            if (body.Width < 700) gap = 7;
            int slotW = (body.Width - 32 - gap * 7) / 8;
            if (slotW < 30) slotW = 30;
            int holeW = slotW;
            if (holeW > 78) holeW = 78;
            int holeH = bodyH - 36;
            if (holeH > 74) holeH = 74;
            if (holeH < 36) holeH = 36;
            // 硬约束 1：孔高绝不能超过琴体高（否则会溢出被裁成扁椭圆）。
            if (holeH > bodyH - 8) holeH = Math.Max(20, bodyH - 8);
            // 硬约束 2：孔宽与孔高比例不超 1:1.35，保证看起来是方形/略高而不是扁圆。
            if (holeW > holeH + holeH / 3) holeW = holeH + holeH / 3;
            if (holeW < 16) holeW = 16;
            // 此时重算布局，保证整体居中。
            int usedW = holeW * 8 + gap * 7;
            int startX = body.X + (body.Width - usedW) / 2;
            int holeY = body.Y + (bodyH - holeH) / 2;
            for (int i = 0; i < 8; i++)
            {
                int x = startX + i * (holeW + gap);
                Rectangle hr = new Rectangle(x, holeY, holeW, holeH);
                holeRects[i] = hr;
                bool on = hole == i;
                Color fill = on ? Color.FromArgb(255, 186, 64) : Color.FromArgb(28, 18, 12);
                // 圆角半径取短边的 1/4（上限 18）：显示为圆角矩形，
                // 而不是之前的半宽半径（那会把琴孔画成椭圆/胶囊）。
                int rr = Math.Min(holeW, holeH) / 4;
                if (rr > 18) rr = 18;
                if (rr < 5) rr = 5;
                using (SolidBrush b = new SolidBrush(fill))
                    RoundRect(g, b, hr, rr);
                using (Pen p = new Pen(Color.FromArgb(40, 26, 16), 2f))
                    RoundRect(g, p, hr, rr);
                string lab = Util.KeyNames[i] + "  " + Util.NoteName(Util.PitchOf(i, oct, mid));
                using (SolidBrush tb = new SolidBrush(on ? Color.Black : Color.FromArgb(255, 230, 180)))
                {
                    SizeF sz = g.MeasureString(lab, holeFont);
                    g.DrawString(lab, holeFont, tb, x + (holeW - sz.Width) / 2, body.Bottom + 6);
                }
            }

            int lampY = Math.Min(harpPanel.Height - 34, body.Bottom + 30);
            DrawMouseLamp(g, "左 低八度", 30, lampY, left);
            DrawMouseLamp(g, "中 升半音", 170, lampY, mid);
            DrawMouseLamp(g, "右 高八度", 310, lampY, right);
            string layer = Util.LayerText(left, mid, right);
            g.DrawString(layer, uiFont, Brushes.Wheat, Math.Max(460, body.Right - 180), lampY + 6);
        }

        // 圆角矩形路径：radius 为圆角半径，自动夹到矩形的一半。
        static void RoundRect(Graphics g, Brush b, Rectangle r, int radius)
        {
            using (GraphicsPath p = MakeRoundPath(r, radius)) g.FillPath(b, p);
        }

        static void RoundRect(Graphics g, Pen p, Rectangle r, int radius)
        {
            using (GraphicsPath path = MakeRoundPath(r, radius)) g.DrawPath(p, path);
        }

        static GraphicsPath MakeRoundPath(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (d < 2) d = 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            int a = d / 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        void DrawMouseLamp(Graphics g, string text, int x, int y, bool on)
        {
            Rectangle r = new Rectangle(x, y, 128, 26);
            using (SolidBrush b = new SolidBrush(on ? Color.FromArgb(220, 140, 40) : Color.FromArgb(60, 44, 32)))
                g.FillRectangle(b, r);
            g.DrawRectangle(Pens.Tan, r);
            g.DrawString(text, uiFont, Brushes.White, x + 8, y + 4);
        }

        void DrawMap(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawString("32 音试听表（可点击）", holeFont, Brushes.Wheat, 10, 8);
            string[] rows = new string[] { "不按", "左键", "中键", "右键" };
            int[,] octs = new int[,] { { 0, 0 }, { -1, 0 }, { 0, 1 }, { 1, 0 } };
            int labelW = 46;
            int availW = Math.Max(120, mapPanel.ClientSize.Width - labelW - 16);
            int availH = Math.Max(96, mapPanel.ClientSize.Height - 60);
            int gapX = 4, gapY = 6;
            // 先按高度算单元格高（上限 46），再取宽/高较小者作为实际尺寸。
            int ch = (availH - gapY * 3) / 4;
            if (ch > 46) ch = 46;
            if (ch < 20) ch = 20;
            int cw = (availW - gapX * 7) / 8;
            if (cw > ch) cw = ch;
            // 最后一道保险：确保 8 列加间距总宽不超过可用宽度，最右列绝不会被裁。
            while (cw > 10 && cw * 8 + gapX * 7 > availW) cw--;
            if (cw < 10) cw = 10;
            for (int r = 0; r < 4; r++)
            {
                g.DrawString(rows[r], uiFont, Brushes.Tan, 8, 44 + r * (ch + gapY));
                for (int c = 0; c < 8; c++)
                {
                    int x = labelW + c * (cw + gapX);
                    int y = 38 + r * (ch + gapY);
                    Rectangle rc = new Rectangle(x, y, cw, ch);
                    cellRects[r * 8 + c] = rc;
                    bool sharp = octs[r, 1] == 1;
                    int midiN = Util.PitchOf(c, octs[r, 0], sharp);
                    bool on = sounding == midiN;
                    using (SolidBrush b = new SolidBrush(on ? Color.FromArgb(255, 186, 64) : Color.FromArgb(58, 42, 30)))
                        g.FillRectangle(b, rc);
                    g.DrawRectangle(Pens.Peru, rc);
                    using (SolidBrush tb = new SolidBrush(on ? Color.Black : Color.Wheat))
                    {
                        if (ch >= 34)
                        {
                            g.DrawString(Util.KeyNames[c], uiFont, tb, x + 4, y + 2);
                            g.DrawString(Util.NoteName(midiN), uiFont, tb, x + 4, y + 16);
                        }
                        else
                        {
                            // 格子变短时只画一行，避免两行文字互相重叠。
                            g.DrawString(Util.KeyNames[c] + Util.NoteName(midiN), uiFont, tb, x + 2, y + ch / 2 - 8);
                        }
                    }
                }
            }
        }

        void MapMouseDown(object sender, MouseEventArgs e)
        {
            int idx = HitCell(e.X, e.Y);
            if (idx < 0) return;
            int r = idx / 8, c = idx % 8;
            int oct = 0;
            bool sharp = false;
            if (r == 1) oct = -1;
            if (r == 2) sharp = true;
            if (r == 3) oct = 1;
            previewMidi = Util.PitchOf(c, oct, sharp);
            RefreshSound();
            mapPanel.Invalidate();
        }

        void MapMouseUp(object sender, MouseEventArgs e)
        {
            previewMidi = -1;
            RefreshSound();
            mapPanel.Invalidate();
        }

        void MapMouseMove(object sender, MouseEventArgs e)
        {
            hoverCell = HitCell(e.X, e.Y);
        }

        int HitCell(int x, int y)
        {
            for (int i = 0; i < cellRects.Length; i++)
                if (cellRects[i].Contains(x, y)) return i;
            return -1;
        }
    }

    public static class Program
    {
        /// <summary>本项目地址（"关于"里点开它）。发布前改成你自己的仓库。</summary>
        public const string ProjectUrl = "https://github.com/USERNAME/harmonica-simulator";

        [STAThread]
        public static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "/export")
            {
                string midi = args.Length >= 2 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sun.mid");
                string dir = args.Length >= 3 ? args[2] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MIDI");
                try
                {
                    Headless.Run(midi, dir);
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "HarpSim-export-error.txt"), ex.ToString());
                    }
                    catch { }
                    Environment.ExitCode = 2;
                }
                return;
            }
            // 单实例：同一时间只允许开一份窗口版。
            // 原因：F5/F6/F7 这类全局热键全系统只能被一个程序占用，
            // 开两份会让后开的那份注册失败，弹出"热键未能注册成功"的误导提示。
            bool created;
            singleInstance = new System.Threading.Mutex(true, SINGLE_INSTANCE_NAME, out created);
            if (!created)
            {
                ActivateExisting();        // 把已经在跑的那份叫到前面来
                return;                    // 自己安静退出，不再开第二个窗口
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                try { System.IO.File.WriteAllText(Path.Combine(Path.GetTempPath(), "HarpSim-crash.txt"), e.Exception.ToString()); } catch { }
                MessageBox.Show(e.Exception.Message + "\r\n\r\n" + e.Exception.StackTrace, "\u5f02\u5e38");
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try { System.IO.File.WriteAllText(Path.Combine(Path.GetTempPath(), "HarpSim-crash.txt"), e.ExceptionObject.ToString()); } catch { }
            };
            try
            {
                Application.Run(new MainForm());
            }
            finally
            {
                try { singleInstance.ReleaseMutex(); } catch { }
                try { singleInstance.Dispose(); } catch { }
            }
        }

        // ---- 单实例相关 ----
        static System.Threading.Mutex singleInstance;
        const string SINGLE_INSTANCE_NAME = "DeltaHarp_SingleInstance_v1";

        /// <summary>把已经在运行的那一份窗口显示出来并前置。</summary>
        static void ActivateExisting()
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName);
                for (int i = 0; i < ps.Length; i++)
                {
                    IntPtr h = ps[i].MainWindowHandle;
                    if (h != IntPtr.Zero)
                    {
                        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                        SetForegroundWindow(h);
                        break;
                    }
                }
            }
            catch { }
        }

        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
    }

    public static class Headless
    {
        public static void Run(string midi, string dir)
        {
            long tot;
            List<ChannelInfo> chs;
            List<MidiNote> all = MidiParser.Parse(midi, out tot, out chs);
            if (chs.Count == 0) throw new Exception("MIDI 没有音符");
            ChannelInfo ch = chs[0];
            List<MidiNote> song = SongBuilder.Build(all, ch.Ch, 0, true);
            List<MacroStep> steps = SongBuilder.MakeMacro(SongBuilder.PlayableOnly(song));
            string title = Path.GetFileNameWithoutExtension(midi);
            if (title.IndexOf("太阳") >= 0 || title.ToLower().IndexOf("sun") >= 0) title = "太阳照常升起";
            Exporter.WriteAll(dir, title, song, steps, ch, 0);
            if (File.Exists(midi))
            {
                string midiCopy = Path.Combine(dir, Path.GetFileName(midi));
                File.Copy(midi, midiCopy, true);
            }
        }
    }

    /// <summary>
    /// "自定义热键"对话框：点一下输入框，直接按想用的键即可。
    /// 支持 F1~F12、字母、数字，以及带 Ctrl / Alt / Shift 的组合键。
    /// </summary>
    public class HotkeyDialog : Form, IMessageFilter
    {
        public Keys KStart = Keys.F6;
        public Keys KBack = Keys.F5;
        public Keys KFwd = Keys.F7;

        TextBox tbStart, tbBack, tbFwd;
        TextBox activeBox;                 // 当前正在设置的那一行
        Label activeHint;                  // 高亮提示

        public HotkeyDialog(Keys start, Keys back, Keys fwd)
        {
            KStart = start; KBack = back; KFwd = fwd;

            Text = "自定义热键";
            ClientSize = new Size(452, 300);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(28, 22, 18);
            ForeColor = Color.FromArgb(240, 228, 200);
            Font = new Font("Microsoft YaHei UI", 9f);

            KeyPreview = true;             // 在对话框任意位置按键都能捕获

            Label tip = new Label();
            tip.Text = "先点一下要改的那一行（会高亮），再直接按你想用的键。\r\n"
                     + "支持 F1~F12、字母、数字，以及 Ctrl / Alt / Shift 组合键。";
            tip.Left = 16; tip.Top = 12; tip.Width = 420; tip.Height = 44;
            tip.ForeColor = Color.FromArgb(200, 180, 140);
            Controls.Add(tip);

            tbBack  = MakeRow("后退 5 秒", 62, KBack);
            tbStart = MakeRow("开始 / 暂停 / 继续", 106, KStart);
            tbFwd   = MakeRow("前进 5 秒", 150, KFwd);

            Label warn = new Label();
            warn.Text = "注意：三个热键要设成不同的键，否则只会生效一个。";
            warn.Left = 16; warn.Top = 188; warn.Width = 420; warn.Height = 22;
            warn.ForeColor = Color.FromArgb(255, 180, 120);
            Controls.Add(warn);

            Button ok = new Button();
            ok.Text = "保存并生效";
            ok.Left = 216; ok.Top = 240; ok.Width = 110; ok.Height = 32;
            ok.DialogResult = DialogResult.OK;
            ok.BackColor = Color.FromArgb(90, 62, 32);
            ok.ForeColor = Color.FromArgb(255, 230, 190);
            ok.FlatStyle = FlatStyle.Flat;
            ok.Click += delegate(object s, EventArgs e)
            {
                KStart = (Keys)tbStart.Tag;
                KBack  = (Keys)tbBack.Tag;
                KFwd   = (Keys)tbFwd.Tag;
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Left = 336; cancel.Top = 240; cancel.Width = 90; cancel.Height = 32;
            cancel.DialogResult = DialogResult.Cancel;
            cancel.BackColor = Color.FromArgb(70, 50, 30);
            cancel.ForeColor = Color.FromArgb(230, 210, 180);
            cancel.FlatStyle = FlatStyle.Flat;
            Controls.Add(cancel);

            SetActive(tbStart);         // 默认选中"开始/暂停"（最常改的就是它）

            // 显示之后再定一次焦点：构造函数阶段控件还没句柄，Focus() 不起作用，
            // 而对话框一显示焦点会自动落到第一个输入框，把默认行带偏。
            Shown += delegate(object s2, EventArgs e)
            {
                try { tbStart.Focus(); } catch { }
                SetActive(tbStart);
                // 消息层拦截：不管焦点落在哪个控件，按键都先到这里。
                // 比 KeyDown / ProcessCmdKey 都可靠（只读输入框会吞掉按键事件）。
                Application.AddMessageFilter(this);
            };
            FormClosed += delegate(object s2, FormClosedEventArgs e)
            {
                try { Application.RemoveMessageFilter(this); } catch { }
            };

            AcceptButton = ok;
            CancelButton = cancel;
        }

        TextBox MakeRow(string caption, int y, Keys cur)
        {
            Label lab = new Label();
            lab.Text = caption;
            lab.Left = 16; lab.Top = y + 5; lab.Width = 152; lab.Height = 22;
            lab.ForeColor = Color.FromArgb(255, 214, 120);
            Controls.Add(lab);

            TextBox tb = new TextBox();
            tb.Left = 176; tb.Top = y; tb.Width = 250; tb.Height = 24;
            tb.ReadOnly = true;                  // 只用来显示，不允许手动输入乱码
            tb.ShortcutsEnabled = false;
            tb.Text = Txt(cur);
            tb.Tag = cur;
            tb.TabStop = true;
            tb.Cursor = Cursors.Hand;
            tb.TextAlign = HorizontalAlignment.Center;
            tb.BackColor = Color.FromArgb(36, 28, 22);
            tb.ForeColor = Color.FromArgb(255, 230, 190);
            // 点这个输入框 = 选中这一行（故意不用 Enter 事件：
            // 对话框一打开焦点会自动落到第一个框，会把默认选中行抢走）
            tb.Click += delegate(object s, EventArgs e) { SetActive(tb); };
            Controls.Add(tb);
            if (activeBox == null) activeBox = tb;      // 默认第一行
            return tb;
        }

        /// <summary>切换当前正在设置的行，并高亮它。</summary>
        void SetActive(TextBox tb)
        {
            activeBox = tb;
            TextBox[] all = new TextBox[] { tbBack, tbStart, tbFwd };
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                bool on = (all[i] == tb);
                all[i].BackColor = on ? Color.FromArgb(96, 68, 30) : Color.FromArgb(36, 28, 22);
            }
        }

        /// <summary>
        /// 消息层拦截按键。
        /// 这是最后一道也是最可靠的一道：只读输入框会吞掉 KeyDown，
        /// ProcessCmdKey 在部分情况下也收不到；而所有键盘消息在派发到控件之前
        /// 一定会先过 IMessageFilter，所以"按了没反应"的问题在这里被彻底解决。
        /// </summary>
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_KEYDOWN = 0x0100;
            const int WM_SYSKEYDOWN = 0x0104;
            if (m.Msg != WM_KEYDOWN && m.Msg != WM_SYSKEYDOWN) return false;

            Keys k = (Keys)(int)m.WParam;
            Keys mods = Control.ModifierKeys;
            if ((mods & Keys.Control) == Keys.Control) k |= Keys.Control;
            if ((mods & Keys.Alt) == Keys.Alt) k |= Keys.Alt;
            if ((mods & Keys.Shift) == Keys.Shift) k |= Keys.Shift;

            // Esc 取消 / Enter 保存
            Keys baseKey = k & ~(Keys.Control | Keys.Alt | Keys.Shift);
            if (baseKey == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (baseKey == Keys.Enter) { DialogResult = DialogResult.OK; Close(); return true; }
            // 只按了修饰键本身不算完整热键，忽略（但仍吃掉，避免乱响）
            if (baseKey == Keys.ControlKey || baseKey == Keys.ShiftKey || baseKey == Keys.Menu ||
                baseKey == Keys.LWin || baseKey == Keys.RWin) return true;

            if (activeBox == null) activeBox = tbStart;
            if (activeBox == null) return false;
            activeBox.Tag = k;
            activeBox.Text = Txt(k);
            SetActive(activeBox);
            return true;      // 吃掉按键，不要变成输入框里乱打字
        }

        static string Txt(Keys k)
        {
            return InputInjector.KeyText(k);
        }
    }
}
