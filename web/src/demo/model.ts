// デモ用データ（scripts/build-demo-data.mjs の出力）の型。API の応答ではなく、時点指定のための履歴を持つ「元データ」。
// デモアダプタはこれを API と同じ規則（概要 §5.6）で投影して返す。

export interface DemoLevel {
  key: string;
  name: string;
  rank: number;
}

export interface DemoLegalEntity {
  id: string;
  code: string;
  name: string;
  short_name: string | null;
}

export interface DemoSite {
  id: string;
  legal_entity_id: string;
  code: string;
  name: string;
  type: "head_office" | "branch" | "sales_office" | "factory" | "laboratory" | "logistics" | "store" | "other";
  /** 都道府県のコード（JIS X 0401）。国外は null。 */
  prefecture_code: string | null;
  /** 事業所の所在地（会社の情報。個人の住所ではない）。 */
  location: string | null;
  valid_from: string;
  valid_until: string | null;
}

export interface DemoOrgUnit {
  id: string;
  legal_entity_id: string;
  code: string;
  name: string;
  short_name: string | null;
  level_key: string | null;
  parent_id: string | null;
  head_person_id: string | null;
  /** 組織自身の拠点（無ければ上位組織の拠点を引き継ぐ）。 */
  site_id: string | null;
  valid_from: string;
  valid_until: string | null;
}

export interface DemoAssignment {
  org_unit_id: string;
  type: "primary" | "concurrent";
  position_title: string | null;
  secondment: "none" | "outbound" | "inbound";
  valid_from: string;
  valid_until: string | null;
}

export interface DemoSuspension {
  from: string;
  until: string | null;
}

export interface DemoPerson {
  id: string;
  employee_number: string;
  legal_entity_id: string;
  family_name: string;
  given_name: string;
  family_name_kana: string | null;
  given_name_kana: string | null;
  family_name_latin: string | null;
  given_name_latin: string | null;
  display_name: string;
  work_email: string | null;
  extension_number: string | null;
  work_mobile_phone: string | null;
  /** 人ごとの拠点（主務の組織の拠点と違う人だけ。null なら主務の組織の拠点）。 */
  site_id: string | null;
  /** リモートワークの対象か（有無だけ。理由・頻度は持たない）。 */
  remote_work: boolean | null;
  employment_type: string | null;
  job_type: string | null;
  hire_date: string;
  termination_date: string | null;
  assignments: DemoAssignment[];
  suspensions: DemoSuspension[];
}

export interface DemoDataset {
  meta: {
    description: string;
    business_date: string;
    derivations: string[];
    milestones: Array<{ date: string; label: string }>;
  };
  legal_entities: DemoLegalEntity[];
  level_schemes: Record<string, DemoLevel[]>;
  sites: DemoSite[];
  org_units: DemoOrgUnit[];
  people: DemoPerson[];
}
