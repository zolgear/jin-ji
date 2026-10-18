// UI が扱うデータの型と、データの取得元（アダプタ）の共通インターフェース。
// 型の元は OpenAPI から生成した schema.gen.ts。UI 側で値を足したり、見えない項目を推測したりしない。
import type { Schemas } from "../api/schema.gen";
import type { CorrectionAdapter } from "./corrections";
import type { WorkplaceAdapter } from "./workplace";

// 建物・階・席・当日の所在の型（data/workplace.ts）
export type * from "./workplace";

/** 人（API の PersonResponse）。見えない項目・値の無い項目は null か省略で来る。 */
export type Person = Schemas["PersonResponse"];
export type Assignment = Schemas["AssignmentResponse"];
export type OrgUnit = Schemas["OrgUnitResponse"];
export type OrgTree = Schemas["OrgTreeResponse"];
/** 人・組織の拠点（inherited は主務の組織・上位組織から引き継いだもの）。 */
export type SiteRef = Schemas["SiteRefResponse"];
/** 拠点の種別（コード値）。 */
export type SiteType = SiteRef["type"];
/** 拠点と、閲覧者に見える人の数（API の SiteResponse）。 */
export type Site = Schemas["SiteResponse"];

/** 人の一覧の 1 ページ。 */
export interface PersonPage {
  items: Person[];
  nextCursor: string | null;
}

/** 組織ごとの人数（主務・兼務・出向）。個人を特定しない集計値。 */
export interface OrgHeadcount {
  primary: number;
  concurrent: number;
  secondment: number;
}

/** 組織をまたぐ所属（兼務・出向）を、組織の組で集計した線。 */
export interface OrgLink {
  /** 主務の組織。 */
  from: string;
  /** 兼務先・出向先の組織。 */
  to: string;
  kind: "concurrent" | "secondment";
  count: number;
}

/** 組織図の集計。API に集計のエンドポイントが無いうちは、API アダプタでは null。 */
export interface OrgStats {
  asOf: string;
  headcount: Record<string, OrgHeadcount>;
  links: OrgLink[];
}

/** 組織の検索結果。 */
export interface OrgSearchResult {
  items: OrgUnit[];
  /** limit を超えた分があるか。 */
  hasMore: boolean;
}

/** 時点のスライダーの目盛り。 */
export interface Milestone {
  date: string;
  label: string;
}

export interface MemberQuery {
  asOf: string;
  includeDescendants?: boolean;
  cursor?: string | null;
  limit?: number;
}

export interface SearchQuery {
  asOf: string;
  cursor?: string | null;
  limit?: number;
}

/** 条件ごとの件数（API の facets）。見える人の見える値だけを数えた値で、UI は補ったり推測したりしない。 */
export type FacetValue = Schemas["FacetValueResponse"];
export type PersonFacets = Schemas["PersonFacetsResponse"];

/** 所属の種類（主務・兼務・出向。所属ごとに 1 つ）。 */
export type AssignmentKind = "primary" | "concurrent" | "secondment";

/** 検索の並び順（API の sort）。 */
export type PersonSort = "id" | "kana" | "org" | "hire_date" | "hire_date_desc" | "site" | "remote_work";

/**
 * 人の検索の条件（API の GET /people の絞り込み）。配列の中は OR、条件同士は AND。
 * 所属の条件（orgId・levels・assignmentKinds・positions）は同じ 1 つの所属で満たす。
 */
export interface PersonFilters {
  query?: string;
  orgId?: string | null;
  includeDescendants?: boolean;
  legalEntities?: string[];
  levels?: string[];
  assignmentKinds?: AssignmentKind[];
  positions?: string[];
  employmentTypes?: string[];
  jobTypes?: string[];
  /** 拠点（拠点の ID）。 */
  sites?: string[];
  /** 拠点の種別（コード値）。 */
  siteTypes?: string[];
  /** 拠点の都道府県（JIS の 2 桁コード）。 */
  prefectures?: string[];
  /** リモートワークの対象か（null・省略は絞り込まない）。 */
  remoteWork?: boolean | null;
  /** 入社年月日の範囲（yyyy-MM-dd、両端を含む）。 */
  hiredFrom?: string | null;
  hiredTo?: string | null;
  employmentStatuses?: string[];
  accountStatuses?: string[];
}

export interface FindPeopleQuery {
  asOf: string;
  filters: PersonFilters;
  sort: PersonSort;
  /** 一致した人数と条件ごとの件数も求める（最初のページだけ。cursor と併用しない）。 */
  facets: boolean;
  cursor?: string | null;
  limit?: number;
}

/** 拠点の一覧の条件（API の GET /sites）。配列の中は OR、条件同士は AND。 */
export interface SiteQuery {
  asOf: string;
  query?: string;
  legalEntities?: string[];
  siteTypes?: string[];
  prefectures?: string[];
}

/** 検索の 1 ページ。total と facets は facets を求めた最初のページだけ。 */
export interface PersonSearchPage extends PersonPage {
  total: number | null;
  facets: PersonFacets | null;
}

/**
 * データの取得元。API アダプタとデモアダプタが同じ形で実装する。
 * 時点（asOf）は yyyy-MM-dd。業務日付（businessDate）より後は人事管理者以外には 403（内示の秘匿）。
 */
export interface DirectoryAdapter {
  readonly kind: "api" | "demo";
  /** 画面に出す取得元の名前。 */
  readonly label: string;
  /** 業務日付（日本時間の今日。デモでは固定）。 */
  readonly businessDate: string;
  /** 時点のスライダーの目盛り（一般の閲覧者には業務日付以前だけ）。 */
  readonly milestones: Milestone[];
  /** 業務日付より後（内示）の時点を指定できるか（人事管理者だけ true）。 */
  readonly canViewFuture: boolean;
  getOrgTree(asOf: string): Promise<OrgTree>;
  getOrgStats(asOf: string): Promise<OrgStats | null>;
  listOrgMembers(orgId: string, query: MemberQuery): Promise<PersonPage>;
  getPerson(id: string, asOf: string): Promise<Person>;
  getManagers(id: string, asOf: string): Promise<Person[]>;
  /** 人の一括取得（最大 100 件。見えない・存在しない ID は省かれる）。 */
  getPeopleByIds(ids: string[], asOf: string): Promise<Person[]>;
  searchOrgs(query: string, options: { asOf: string; limit?: number }): Promise<OrgSearchResult>;
  searchPeople(query: string, options: SearchQuery): Promise<PersonPage>;
  /** 人の多角的な検索（絞り込み・並べ替え・件数）。照合・件数は見える項目だけ（API 側の規則）。 */
  findPeople(query: FindPeopleQuery): Promise<PersonSearchPage>;
  /** 拠点の一覧（法人のコード・拠点コードの順）。人数は見える人だけ（API の規則）。 */
  listSites(query: SiteQuery): Promise<Site[]>;
  /** 拠点の詳細。その時点に無ければ 404。 */
  getSite(id: string, asOf: string): Promise<Site>;
  /** 修正依頼（起票しても公開データは変わらない。data/corrections.ts）。 */
  readonly corrections: CorrectionAdapter;
  /** 建物・階・席と当日の所在（data/workplace.ts）。 */
  readonly workplace: WorkplaceAdapter;
}

/** API の Problem Details を伴うエラー。 */
export class ApiError extends Error {
  readonly status: number;
  readonly problemType: string | null;
  /** 429 の Retry-After（秒）。 */
  readonly retryAfter: number | null;

  constructor(status: number, message: string, problemType: string | null = null, retryAfter: number | null = null) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.problemType = problemType;
    this.retryAfter = retryAfter;
  }
}

/** 利用者に見せるエラーの説明。 */
export function describeError(error: unknown): string {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 400:
        return `入力が正しくありません: ${error.message}`;
      case 401:
        return "ログインの有効期限が切れました。ページを再読み込みしてログインし直してください。";
      case 403:
        return "この操作・時点は参照できません（未来の時点は人事管理者だけが参照できます）。";
      case 404:
        return "見つからないか、参照できません。";
      case 429:
        return "リクエストが多すぎます。しばらく待ってから再度お試しください。";
      default:
        return `エラーが発生しました（${error.status}）: ${error.message}`;
    }
  }
  return error instanceof Error ? error.message : String(error);
}
