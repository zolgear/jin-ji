// 検索語の検査と、デモ・組織名の簡易一致。
// 本番の人の検索は API 側の正規化（SearchKey: 異体字・かなの揺れを吸収）で行う。ここの正規化は簡易なもの。

/** API が拒否するワイルドカード。 */
const WILDCARDS = /[*%?]/;

/** 簡易の検索キー: NFKC・小文字・空白除去・カタカナをひらがなへ。 */
export function simpleSearchKey(text: string | null | undefined): string {
  return (text ?? "")
    .normalize("NFKC")
    .toLowerCase()
    .replace(/[\s　・･]/g, "")
    .replace(/[ァ-ヶ]/g, (c) => String.fromCharCode(c.charCodeAt(0) - 0x60));
}

/** API と同じ条件で検索語を検査する。問題が無ければ null。 */
export function validateQuery(query: string): string | null {
  if (WILDCARDS.test(query)) {
    return "ワイルドカード（* % ?）は使えません。";
  }
  if (simpleSearchKey(query).length < 2) {
    return "2 文字以上で入力してください。";
  }
  return null;
}

/** 候補のいずれかに検索キーが部分一致するか。 */
export function matchesAny(query: string, candidates: Array<string | null | undefined>): boolean {
  const key = simpleSearchKey(query);
  if (!key) return false;
  return candidates.some((c) => simpleSearchKey(c).includes(key));
}
