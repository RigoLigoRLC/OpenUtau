using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Plugin.Builtin {
    [Phonemizer("Chinese VOCALOID Phonemizer", "ZH VOCALOID", language: "ZH")]
    public class ChineseVocaloidPhonemizer : Phonemizer {
        private static Tuple<string, string, string[]>[] specialVowels = {
                new Tuple<string, string, string[]>("u", "y", new []{ "j", "q", "x", "y" }),
                new Tuple<string, string, string[]>("v", "y", new []{ "n", "l" }),
                new Tuple<string, string, string[]>("i", "i\\", new []{ "z", "c", "s" }),
                new Tuple<string, string, string[]>("i", "i`", new []{ "zh", "ch", "sh", "r" }),
                new Tuple<string, string, string[]>("ue", "yE_r", new []{ "j", "q", "x", "y" }),
                new Tuple<string, string, string[]>("ve", "yE_r", new []{ "n", "l" }),
                new Tuple<string, string, string[]>("un", "y_n", new []{ "j", "q", "x", "y" }),
                new Tuple<string, string, string[]>("uan", "y{_n", new []{ "j", "q", "x", "y" }),
            };

        private static string[] specialVowelList;

        private static Dictionary<string, string> vowelMap = new Dictionary<string, string>();
        private static Dictionary<string, string> consonantMap = new Dictionary<string, string>();
        private USinger singer;
        
        // Voicebank specific data
        // Articulation combination availability. Only diphoneme articulation is supported now;
        // Use articulation combination as index to obtain different pitches, then get filename
        // and section information in specific pitch of an articulation
        // VOCALOID articulation section timing
        // includes 4 values, beginFrame & endFrame correspond to FRM2 frames of this section
        // stationary sections are concatenated as is. the part not covered by stationary section
        // can be overlapped with next or previous note.
        // We further simplify this down to three properties:
        // SampleLength, HeadOverlap, TailOverlap
        struct SegmentAlias {
            public string Alias;
            public double SampleLength, HeadOverlap, TailOverlap, Preutterance;
            public bool IsArticulation;
        }
        
        //                <Articulation        -->       <MidiPitch --> PitchSegment>>
        private Dictionary<Tuple<string, string>, Dictionary<int, SegmentAlias>> possibleArticulation = 
            new Dictionary<Tuple<string, string>, Dictionary<int, SegmentAlias>>();

        private Dictionary<string, Dictionary<int, SegmentAlias>> possibleStationary =
            new Dictionary<string, Dictionary<int, SegmentAlias>>();

        private static readonly string vowelXSampaMapping =
            // Generic vowels
            "a:a o:o e:7 i:i u:u " +
            "er:@` ai:aI ei:ei ao:AU ou:@U ia:ia ya:ia ie:iE_r ye:iE_r " +
            "ua:ua wa:ua uo:uo wo:uo iao:iAU yao:iAU iu:i@U you:i@U uai:uaI wai:uaI ui:uei wei:uei " +
            "an:a_n en:@_n in:i_n yin:i_n ian:iE_n yan:iE_n uan:ua_n wan:ua_n un:u@_n wen:u@_n " +
            "ang:AN eng:@N ing:iN ying:iN iang:iAN yang:iAN uang:uAN wang:uAN weng:u@N " +
            "ong:UN iong:iUN yong:iUN";
        private static readonly string consonantXSampaMapping =
            // Consonants
            "b:p p:p_h m:m f:f d:t t:t_h n:n l:l g:k k:k_h h:x j:ts\\ q:ts\\_h x:s\\ " +
            "zh:ts` ch:ts`_h sh:s` r:z` z:ts c:ts_h s:s";

        static ChineseVocaloidPhonemizer() {
            specialVowelList = specialVowels.Select(tup=> tup.Item1).ToArray();

            vowelMap = vowelXSampaMapping.Split(' ').ToList().Select(relation =>
                relation.Split(':')
            ).ToDictionary(t => t[0], t => t[1]);
            
            consonantMap = consonantXSampaMapping.Split(' ').ToList().Select(relation =>
                relation.Split(':')
            ).ToDictionary(t => t[0], t => t[1]);
        }

        public override void SetSinger(USinger singer) {
            if (this.singer == singer)
                return;
            
            Log.Information("VocaloidPhonemizer begin constructing");

            this.singer = singer;
            possibleArticulation.Clear();
            // Load data file, to obtain articulation availability, pitch, sections
            try {
                string file = Path.Combine(singer.Location, "SECTIONS.CSV");
                using (var reader = new StreamReader(file, singer.TextFileEncoding)) {
                    while (!reader.EndOfStream) {
                        var line = reader.ReadLine();
                        line = System.Text.RegularExpressions.Regex.Unescape(line);
                        var parts = line.Split(',');

                        switch (parts[0]) {
                            case "0": // Stationary
                            {
                                var phoneme = parts[3];
                                // Obtain pitch
                                var pitch = int.Parse(parts[5]);
                                var pitchSeg = new SegmentAlias();
                                pitchSeg.Alias = parts[4];
                                pitchSeg.IsArticulation = false;
                                possibleStationary.TryAdd(phoneme, new Dictionary<int, SegmentAlias>());
                                possibleStationary[phoneme][pitch] = pitchSeg;
                                break;
                            }
                            case "1": // Articulation
                            {
                                // Obtain phoneme tuple
                                var phonemes = parts[3].Split(' ');
                                var phonemeTuple = new Tuple<string, string>(phonemes[0], phonemes[1]);
                                // Obtain pitch
                                var pitch = int.Parse(parts[5]);
                                // We only care about the beginning section and enc section.
                                var sectionCount = (parts.Length - 7) / 4;
                                // HeadOverlap
                                var headSectionBegin = Double.Parse(parts[7]);
                                var headStationaryBegin = Double.Parse(parts[9]);
                                // TailOverlap
                                var tailSectionEnd = Double.Parse(parts[8 + (sectionCount - 1) * 4]);
                                var tailStationaryEnd = Double.Parse(parts[10 + (sectionCount - 1) * 4]);
                                // Preutterance
                                var headSectionEnd = Double.Parse(parts[8]);

                                var pitchSeg = new SegmentAlias();
                                pitchSeg.Alias = parts[4];
                                pitchSeg.SampleLength = Double.Parse(parts[6]);
                                pitchSeg.HeadOverlap = headStationaryBegin - headSectionBegin;
                                pitchSeg.TailOverlap = tailSectionEnd - tailStationaryEnd;
                                pitchSeg.Preutterance = headSectionEnd;
                                pitchSeg.IsArticulation = true;

                                possibleArticulation.TryAdd(phonemeTuple, new Dictionary<int, SegmentAlias>());
                                possibleArticulation[phonemeTuple][pitch] = pitchSeg;
                                break;
                            }
                            default:
                                break;
                        }
                        
                    }
                }
            } catch (Exception e) {
                Log.Error(e, "failed to load SECTIONS.CSV");
            }
            Log.Information("VocaloidPhonemizer constructed");
        }

        private Result Subprocess(Note note) {
            var grapheme = note.lyric;
            string? vowel = null, consonant = null;

            // Try to find a suitable special vowel for it
            foreach (var v in specialVowels) {
                if (grapheme.EndsWith(v.Item1) &&
                    v.Item3.Contains(grapheme.Substring(0, grapheme.Length - v.Item1.Length))) {
                    vowel = v.Item2;
                    grapheme = grapheme.Substring(0, grapheme.Length - v.Item1.Length); // Delete vowel
                    break;
                }
            }
            // If there's no special vowel matched, just find a general vowel
            if (vowel is null) {
                for (var i = grapheme.Length; i > 0; i--) {
                    if (vowelMap.TryGetValue(grapheme.Substring(grapheme.Length - i, i), out vowel)) {
                        grapheme = grapheme.Substring(0, grapheme.Length - i);
                        break;
                    }
                }
            }
            // If there's consonant left, find one
            if (grapheme.Length != 0) {
                consonantMap.TryGetValue(grapheme, out consonant);
            }

            // Give up
            if (consonant is null && vowel is null)
                return MakeSimpleResult(grapheme);
            if (consonant is null)
                return MakeSimpleResult(vowel);
            return new Result {
                phonemes = new Phoneme[] {
                    new Phoneme() {
                        phoneme = consonant
                    },
                    new Phoneme() {
                        phoneme = vowel
                    }
                }
            };
        }

        private SegmentAlias ChooseAppropriatePitch(ref Dictionary<int, SegmentAlias> pitches, int pitch) {
            return pitches.Aggregate((kv1, kv2) =>
                Math.Abs(kv1.Key - pitch) < Math.Abs(kv2.Key - pitch) ? kv1 : kv2).Value;
        }

        public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevs) {
            // The first phoneme of the next note, is needed to induce
            // the CV-V-VC form concatenation unit of VOCALOID
            //  MyConsonant->MyVowel->NextConsonant
            string? prevPhoneme = null, nextPhoneme = null;
            List<SegmentAlias> ret = new List<SegmentAlias>();

            if (prev is null) {
                // This is checked to ensure the first note can have Sil- part
                prevPhoneme = "Sil";
            }

            if (next is null)
                nextPhoneme = "Sil";
            else if (next.Value.lyric == "R") {
                nextPhoneme = "Sil";
                next = null;
            } else {
                nextPhoneme = Subprocess(next.Value).phonemes.First().phoneme;
            }

            var currentResult = Subprocess(notes[0]);
            // Discrete phoenmes
            List<string> discretePhonemes = new List<string>();
            
            if(!(prevPhoneme is null)) discretePhonemes.Add(prevPhoneme);
            foreach (var ph in currentResult.phonemes) discretePhonemes.Add(ph.phoneme);
            if(!(nextPhoneme is null)) discretePhonemes.Add(nextPhoneme);
            // An example
            // |  Sil  |  x  |  aI  |  ts`  |
            // |Sil>x|x>aI|  aI  |aI>ts`| the rest belongs to next note
            
            // Begin building phonemes
            Dictionary<int, SegmentAlias>? pitches;

            for (var i = 0; i + 1 < discretePhonemes.Count; i++) {
                if (possibleStationary.TryGetValue(discretePhonemes[i], out pitches)) {
                    ret.Add(ChooseAppropriatePitch(ref pitches, notes[0].tone));
                }
                if (possibleArticulation.TryGetValue(
                        new Tuple<string, string>(discretePhonemes[i], discretePhonemes[i + 1]), out pitches)) {
                    ret.Add(ChooseAppropriatePitch(ref pitches, notes[0].tone));
                }
            }

            // Correct timings
            var result = ret.Select(i => new Phoneme { phoneme = i.Alias }).ToArray();
            // Strategy: TYPICALLY we have ONLY ONE Stationary segment. We look for it, and it MUST have a preceding
            // alias. We use this alias' Preutterance time as ZERO time. And since in Vocaloid, articulation is not
            // stretched, the onset times of other aliases can be easily determined. Can further propagate to left side.
            // Search for stationary segment
            int idxVowel = 0;
            double vowelOnset = 0.0, precedingAliasOnset = 0.0, propagatedOnset = 0.0;
            while (idxVowel < ret.Count && ret[idxVowel].IsArticulation) idxVowel++;
            if (idxVowel == ret.Count)
                // FIXME: No vowels found...
                return new Result { phonemes = result };
            var precedingAlias = ret[idxVowel - 1];
            precedingAliasOnset = -precedingAlias.Preutterance;
            vowelOnset = precedingAliasOnset + precedingAlias.SampleLength - precedingAlias.TailOverlap;
            result[idxVowel].position = MsToTick(vowelOnset * 1000);
            result[idxVowel - 1].position = MsToTick(precedingAliasOnset * 1000);
            // Propagate to left side
            propagatedOnset = precedingAliasOnset;
            for (int artPropg = idxVowel - 2; artPropg >= 0; artPropg--) {
                var thisAlias = ret[artPropg];
                propagatedOnset -= thisAlias.SampleLength - thisAlias.TailOverlap;
                result[artPropg].position = MsToTick(propagatedOnset * 1000);
                propagatedOnset += thisAlias.HeadOverlap;
            }
            // Right side propagation
            propagatedOnset = TickToMs(notes[0].duration) / 1000;
            for (int artPropg = ret.Count - 1; artPropg > idxVowel; artPropg--) {
                var thisAlias = ret[artPropg];
                propagatedOnset -= thisAlias.SampleLength - thisAlias.TailOverlap;
                result[artPropg].position = MsToTick(propagatedOnset * 1000);
                propagatedOnset += thisAlias.HeadOverlap;
            }

            return new Result {
                phonemes = result
            };
        }
    }
}
