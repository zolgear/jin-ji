// 人名の「読み」の照合キー。ひらがな・カタカナ・半角カナ・全角英字・ローマ字の綴りの揺れ（ヘボン式・訓令式・ワープロ式）と長音・促音の有無を吸収する。
// 規則は API 側の ReadingKey（src/JinJi.Domain/Text/ReadingKey.cs）と同じ。両者の一致は共有のテストベクタ（readingKey.vectors.json）で確かめる。

/** 照合に使う最小の長さ（これより短いキーは部分一致に使わない）。 */
export const READING_MIN_MATCH_LENGTH = 3;

/** ぁ（U+3041）〜ゖ（U+3096）のローマ字。小さい ぁぃぅぇぉ・ゃゅょ・っ は空（別に扱う）。 */
const ROMAJI: readonly string[] = [
  "", "a", "", "i", "", "u", "", "e", "", "o",
  "ka", "ga", "ki", "gi", "ku", "gu", "ke", "ge", "ko", "go",
  "sa", "za", "si", "zi", "su", "zu", "se", "ze", "so", "zo",
  "ta", "da", "ti", "zi", "", "tu", "zu", "te", "de", "to", "do",
  "na", "ni", "nu", "ne", "no",
  "ha", "ba", "pa", "hi", "bi", "pi", "hu", "bu", "pu", "he", "be", "pe", "ho", "bo", "po",
  "ma", "mi", "mu", "me", "mo",
  "", "ya", "", "yu", "", "yo",
  "ra", "ri", "ru", "re", "ro",
  "wa", "wa", "i", "e", "o", "n", "vu", "ka", "ke",
];

/** 英字の綴りの正規化（この順に当てる）。 */
const SPELLINGS: ReadonlyArray<readonly [string, string]> = [
  ["tch", "ch"], ["shi", "si"], ["sh", "sy"], ["chi", "ti"], ["ch", "ty"], ["tsu", "tu"],
  ["ji", "zi"], ["j", "zy"], ["f", "h"], ["c", "k"], ["l", "r"],
];

/** 長音として畳む母音の重なり（変化が無くなるまで繰り返す）。 */
const LONG_VOWELS: ReadonlyArray<readonly [string, string]> = [
  ["ou", "o"], ["oo", "o"], ["uu", "u"], ["aa", "a"], ["ii", "i"], ["ee", "e"],
];

const SMALL_Y: Readonly<Record<string, string>> = { "ゃ": "a", "ゅ": "u", "ょ": "o" };
const SMALL_VOWELS: Readonly<Record<string, string>> = { "ぁ": "a", "ぃ": "i", "ぅ": "u", "ぇ": "e", "ぉ": "o" };

/** 空白（C# の char.IsWhiteSpace と同じ範囲。\s に無い U+0085 を足す）と、読みの区切りの記号。 */
const IGNORED = /[\s\u0085・\-‐'’.,]/;

/** ゼロ幅の文字（ZWSP・ZWNJ・ZWJ・WJ・BOM。SearchKey.IsZeroWidth と同じ）。 */
const ZERO_WIDTH = new Set(["\u200b", "\u200c", "\u200d", "\u2060", "\ufeff"]);

const isVowel = (c: string | undefined): boolean => c === "a" || c === "i" || c === "u" || c === "e" || c === "o";

/** 読みの照合キー。読みとして扱えない値（漢字・数字・記号を含む）や空なら null。 */
export function readingKey(text: string | null | undefined): string | null {
  if (text == null) return null;

  const out: string[] = [];
  // 直前の 1 文字ぶんの出力の開始位置（小さいかなが直前の音を書き換えるため）
  let chunkStart = 0;
  for (const raw of text.normalize("NFKC")) {
    // 英字の小文字化は ASCII だけ（ほかの文字はどのみち null にするため。C# 側と結果をそろえる）
    let c = raw >= "A" && raw <= "Z" ? raw.toLowerCase() : raw;
    if (IGNORED.test(c) || ZERO_WIDTH.has(c)) continue;

    if (c >= "a" && c <= "z") {
      chunkStart = out.length;
      out.push(c);
      continue;
    }

    if (c >= "ァ" && c <= "ヶ") {
      c = String.fromCharCode(c.charCodeAt(0) - 0x60);
    } else if (c === "ー") {
      chunkStart = out.length;
      continue;
    } else if (!(c >= "ぁ" && c <= "ゖ")) {
      return null;
    }

    const chunkLength = out.length - chunkStart;
    const smallY = SMALL_Y[c];
    const smallVowel = SMALL_VOWELS[c];
    if (c === "っ") {
      // 促音は何も出さない（後段の同じ子音の畳み込みで、促音の有無を同一視する）
      chunkStart = out.length;
    } else if (smallY !== undefined) {
      if (chunkLength >= 2 && out[out.length - 1] === "i") {
        // きゃ → kya、しゃ → sya
        out[out.length - 1] = "y";
        out.push(smallY);
      } else {
        chunkStart = out.length;
        out.push("y", smallY);
      }
    } else if (smallVowel !== undefined) {
      if (chunkLength >= 2 && isVowel(out[out.length - 1]) && !isVowel(out[out.length - 2])) {
        // ふぁ → ha、てぃ → ti、ゔぁ → va
        out[out.length - 1] = smallVowel;
      } else {
        chunkStart = out.length;
        out.push(smallVowel);
      }
    } else {
      chunkStart = out.length;
      out.push(...(ROMAJI[c.charCodeAt(0) - 0x3041] ?? ""));
    }
  }

  if (out.length === 0) return null;

  // 綴りの正規化と畳み込みは、結果が変わらなくなるまで繰り返す（畳み込みで新しくできた綴りも正規化し、冪等にする）
  let key = out.join("");
  for (;;) {
    const next = fold(normalizeSpelling(key));
    if (next === key) break;
    key = next;
  }
  return key.length === 0 ? null : key;
}

/** ヘボン式・ワープロ式などの綴りを訓令式寄りにそろえる。 */
function normalizeSpelling(value: string): string {
  let result = value;
  for (const [from, to] of SPELLINGS) result = result.replaceAll(from, to);
  // 撥音の m（Namba・Homma）を n にそろえる
  return result.replace(/m(?=[bpm])/g, "n");
}

/** 語中・語末の oh、長音の母音の重なり、同じ子音の重なりを畳む。 */
function fold(value: string): string {
  let result = value.replace(/oh(?![aiueoy])/g, "o");
  let previous: string;
  do {
    previous = result;
    for (const [from, to] of LONG_VOWELS) result = result.replaceAll(from, to);
  } while (result !== previous);
  return result.replace(/([bcdfghjklmnpqrstvwxz])\1+/g, "$1");
}
