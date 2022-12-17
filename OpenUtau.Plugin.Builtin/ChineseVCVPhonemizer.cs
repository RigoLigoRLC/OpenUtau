using System.Collections.Generic;
using System.Linq;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.Builtin {
    [Phonemizer("Chinese VCV (樗儿式中文连续音) Phonemizer", "ZH VCV", language: "ZH")]
    public class ChineseVCVPhonemizer : Phonemizer {
        // Note: This ZH-VCV Phonemizer is largely built upon JA-VCV's existing code.

        private USinger singer;

        private static readonly string[] codaDict = new string[] {
            "a=a,ba,pa,ma,fa,da,ta,na,la,ga,ka,ha,zha,cha,sha,za,ca,sa,ya,lia,jia,qia,xia,wa,gua,kua,hua,zhua,shua,dia",
            "ai=ai,bai,pai,mai,dai,tai,nai,lai,gai,kai,hai,zhai,chai,shai,zai,cai,sai,wai,guai,kuai,huai,zhuai,chuai,shuai",
            "an=an,ban,pan,man,fan,dan,tan,nan,lan,gan,kan,han,zhan,chan,shan,ran,zan,can,san,wan,duan,tuan,nuan,luan,guan,kuan,huan,zhuan,chuan,shuan,ruan,zuan,cuan,suan",
            "ang=ang,bang,pang,mang,fang,dang,tang,nang,lang,gang,kang,hang,zhang,chang,shang,rang,zang,cang,sang,yang,liang,jiang,qiang,xiang,wang,guang,kuang,huang,zhuang,chuang,shuang,niang",
            "ao=ao,bao,pao,mao,dao,tao,nao,lao,gao,kao,hao,zhao,chao,shao,rao,zao,cao,sao,yao,biao,piao,miao,diao,tiao,niao,liao,jiao,qiao,xiao",
            "e=e,me,de,te,ne,le,ge,ke,he,zhe,che,she,re,ze,ce,se",
            "ei=ei,bei,pei,mei,fei,dei,tei,nei,lei,gei,kei,hei,zhei,shei,zei,wei,dui,tui,gui,kui,hui,zhui,chui,shui,rui,zui,cui,sui",
            "en=en,ben,pen,men,fen,nen,gen,ken,hen,zhen,chen,shen,ren,zen,cen,sen,wen,dun,tun,lun,gun,kun,hun,zhun,chun,shun,run,zun,cun,sun",
            "eng=eng,beng,peng,meng,feng,deng,teng,neng,leng,geng,keng,heng,weng,zheng,cheng,sheng,reng,zeng,ceng,seng",
            "er=er",
            "i=i,bi,pi,mi,di,ti,ni,li,ji,qi,xi,yi",
            "ian=yan,bian,pian,mian,dian,tian,nian,lian,jian,qian,xian,yuan,juan,quan,xuan",
            "ie=ye,bie,pie,mie,die,tie,nie,lie,jie,qie,xie",
            "in=yin,bin,pin,min,nin,lin,jin,qin,xin",
            "ing=ying,bing,ping,ming,ding,ting,ning,ling,jing,qing,xing",
            "ir=zhi,chi,shi,ri",
            "iz=zi,ci,si",
            "o=o,bo,po,mo,fo,wo,duo,tuo,nuo,luo,guo,kuo,huo,zhuo,chuo,shuo,ruo,zuo,cuo,suo",
            "ong=ong,dong,tong,nong,long,gong,kong,hong,zhong,chong,rong,zong,cong,song,yong,jiong,qiong,xiong",
            "ou=ou,pou,mou,fou,dou,tou,lou,gou,kou,hou,zhou,chou,shou,rou,zou,cou,sou,you,miu,diu,niu,liu,jiu,qiu,xiu",
            "u=u,bu,pu,mu,fu,du,tu,nu,lu,gu,ku,hu,zhu,chu,shu,ru,zu,cu,su,wu",
            "ue=yue,nue,lue,jue,que,xue",
            "v=yu,nv,lv,ju,qu,xu",
            "vn=yun,jun,qun,xun",
        };

        private static readonly Dictionary<string, string> codaMapping;

        static ChineseVCVPhonemizer() {
            // Converts the lookup table from raw strings to a dictionary for better performance.
            // From JA-VCV
            codaMapping = codaDict.ToList()
                .SelectMany(line => {
                    var parts = line.Split('=');
                    return parts[1].Split(',').Select(cv => (cv, parts[0]));
                })
                .ToDictionary(t => t.Item1, t => t.Item2);
        }

        public override void SetSinger(USinger singer) => this.singer = singer;

        public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevs) {
            var note = notes[0];
            var lyric = note.lyric;
            string phoneme = string.Empty;

            // When the previous note is present, get it's content and find corresponding coda
            if (prevNeighbour != null) {
                var prevLyrics = prevNeighbour?.lyric;
                if (codaMapping.TryGetValue(prevLyrics ?? string.Empty, out var coda)) {
                    phoneme = $"{coda} {lyric}";
                }
            } else {
                // Default state, just current phoneme
                phoneme = $"- {lyric}";
            }
            
            // Get color
            string color = string.Empty;
            int toneShift = 0;
            if (note.phonemeAttributes != null) {
                var attr = note.phonemeAttributes.FirstOrDefault(attr => attr.index == 0);
                color = attr.voiceColor;
                toneShift = attr.toneShift;
            }
            if (singer.TryGetMappedOto(phoneme, note.tone + toneShift, color, out var oto)) {
                phoneme = oto.Alias;
            } else if (singer.TryGetMappedOto(note.lyric, note.tone + toneShift, color, out oto)) {
                phoneme = oto.Alias;
            } else {
                phoneme = note.lyric;
            }
            return new Result {
                phonemes = new Phoneme[] {
                    new Phoneme {
                        phoneme = phoneme,
                    }
                },
            };
        }
    }
}
