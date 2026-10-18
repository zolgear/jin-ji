// コード値の表示名。値そのものは API のコード値のまま持ち、表示だけを日本語にする。

/** 雇用区分（DB の person_version.employment_type のコード値）。 */
export const EMPLOYMENT_TYPE_LABEL: Record<string, string> = {
  regular: "正社員",
  contract: "契約社員",
  entrusted: "嘱託",
  dispatched: "派遣",
  seconded_in: "出向受入",
  part_time: "パート・アルバイト",
  other: "その他",
};

export const EMPLOYMENT_STATUS_LABEL: Record<string, string> = { active: "在籍", terminated: "退職" };

export const ACCOUNT_STATUS_LABEL: Record<string, string> = { active: "有効", suspended: "一時停止中" };

export const ASSIGNMENT_KIND_LABEL: Record<string, string> = { primary: "主務", concurrent: "兼務", secondment: "出向" };

/** 雇用区分の表示名（知らないコードはそのまま）。 */
export function employmentTypeLabel(code: string | null | undefined): string {
  return code ? (EMPLOYMENT_TYPE_LABEL[code] ?? code) : "";
}

/** 拠点の種別（JinJi.Domain の SiteCodes.Types）。並びは表示の順。 */
export const SITE_TYPES = ["head_office", "branch", "sales_office", "factory", "laboratory", "logistics", "store", "other"] as const;

export const SITE_TYPE_LABEL: Record<string, string> = {
  head_office: "本社",
  branch: "支社・支店",
  sales_office: "営業所",
  factory: "工場",
  laboratory: "研究所",
  logistics: "物流拠点",
  store: "店舗",
  other: "その他",
};

/** 拠点の種別の表示名（知らないコードはそのまま）。 */
export function siteTypeLabel(code: string | null | undefined): string {
  return code ? (SITE_TYPE_LABEL[code] ?? code) : "";
}

/** 都道府県（JIS X 0401 のコード順）。 */
export const PREFECTURES: ReadonlyArray<readonly [code: string, name: string]> = [
  ["01", "北海道"], ["02", "青森県"], ["03", "岩手県"], ["04", "宮城県"], ["05", "秋田県"], ["06", "山形県"], ["07", "福島県"],
  ["08", "茨城県"], ["09", "栃木県"], ["10", "群馬県"], ["11", "埼玉県"], ["12", "千葉県"], ["13", "東京都"], ["14", "神奈川県"],
  ["15", "新潟県"], ["16", "富山県"], ["17", "石川県"], ["18", "福井県"], ["19", "山梨県"], ["20", "長野県"], ["21", "岐阜県"],
  ["22", "静岡県"], ["23", "愛知県"], ["24", "三重県"], ["25", "滋賀県"], ["26", "京都府"], ["27", "大阪府"], ["28", "兵庫県"],
  ["29", "奈良県"], ["30", "和歌山県"], ["31", "鳥取県"], ["32", "島根県"], ["33", "岡山県"], ["34", "広島県"], ["35", "山口県"],
  ["36", "徳島県"], ["37", "香川県"], ["38", "愛媛県"], ["39", "高知県"], ["40", "福岡県"], ["41", "佐賀県"], ["42", "長崎県"],
  ["43", "熊本県"], ["44", "大分県"], ["45", "宮崎県"], ["46", "鹿児島県"], ["47", "沖縄県"],
];

const PREFECTURE_NAME = new Map(PREFECTURES);

/** 都道府県のコードから名前（知らないコードは null）。 */
export function prefectureName(code: string | null | undefined): string | null {
  return code ? (PREFECTURE_NAME.get(code) ?? null) : null;
}

/** リモートワークの有無の表示名。 */
export const REMOTE_WORK_LABEL: Record<string, string> = { true: "リモートワーク対象", false: "対象外" };
