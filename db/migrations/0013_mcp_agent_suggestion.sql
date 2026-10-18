-- 0013: MCP のエージェントによる要確認の提案への推定（アーキテクチャ概要 §7.1・§8「MCP のエージェントが推定を書き込む口」）
--
-- 位置づけ
-- - 要確認（needs_review）の提案に、AI エージェント（MCP の提案ツール suggest_review_resolution）が「どう解決するのがよいか」の推定を添える。
--   公開されない作業領域への自動の書き込みで、承認は要らない（§7.1）。提案の状態・値は変えない
-- - 公開側に出るのは、人（人事管理者）がこの推定を参考に解決し、解決者とも取込の実行者とも別の人事管理者が承認したときだけ（0008・0010 のまま）
-- - LLM による推定（change_proposal.basis の llm。取込の時点で付ける）と違い、取込の後から何度でも足せる。提案の中身は不変（0005 のトリガー）なので別の表に持つ
--
-- 守り
-- - 追記専用（UPDATE・DELETE の権限を与えない）。参照は hr_admin とバッチの実行者本人だけ（他の作業領域の表と同じ）
-- - 追加できるのは AI エージェントの閲覧者だけで、記録する委任元は閲覧者本人（委任元）、委任元は hr_admin かバッチの実行者本人（RLS）
-- - 提案と取込単位の対応は複合外部キーで縛る（RLS の判定に使う import_batch_id を偽れない）
-- - 選んだ人・組織が提案の候補にあること、提案が要確認であることはアプリ（ProposalSuggestionService）が確かめる
--   （他の行を読むトリガーは RLS で閲覧者により結果が変わるので書かない。§5.6）
-- - 理由（rationale）はエージェントが書いた自由記述で、500 文字まで・制御文字なし。人の画面ではデータとして表示し、指示として扱わない

-- 複合外部キーの参照先（提案の ID と取込単位の組）
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_id_batch_key UNIQUE (id, import_batch_id);

CREATE TABLE proposal_agent_suggestion (
    id                      uuid         PRIMARY KEY,
    change_proposal_id      uuid         NOT NULL,
    import_batch_id         uuid         NOT NULL,
    resolution              text         NOT NULL,
    person_id               uuid         REFERENCES person (id),
    org_unit_id             uuid         REFERENCES org_unit (id),
    confidence              numeric(4,3) NOT NULL,
    rationale               text         NOT NULL,
    suggested_by_person_id  uuid         NOT NULL REFERENCES person (id),
    suggested_via_client_id text         NOT NULL,
    created_at              timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT proposal_agent_suggestion_proposal_fkey
        FOREIGN KEY (change_proposal_id, import_batch_id) REFERENCES change_proposal (id, import_batch_id),
    -- 解決の種類は人の解決と同じ 4 つ（候補から選ぶ / 新規として登録 / 修正依頼に回す / 却下）
    CONSTRAINT proposal_agent_suggestion_resolution_check CHECK (resolution IN ('select_candidate', 'register_new', 'refer_to_correction', 'reject')),
    -- 候補から選ぶときだけ人か組織を指す
    CONSTRAINT proposal_agent_suggestion_choice_check CHECK (
        (resolution = 'select_candidate') = (person_id IS NOT NULL OR org_unit_id IS NOT NULL)),
    CONSTRAINT proposal_agent_suggestion_confidence_check CHECK (confidence >= 0 AND confidence <= 1),
    CONSTRAINT proposal_agent_suggestion_rationale_check CHECK (
        btrim(rationale) <> '' AND char_length(rationale) <= 500 AND rationale !~ '[[:cntrl:]]'),
    CONSTRAINT proposal_agent_suggestion_client_not_blank CHECK (btrim(suggested_via_client_id) <> '' AND char_length(suggested_via_client_id) <= 2048)
);
COMMENT ON TABLE proposal_agent_suggestion IS 'AI エージェントによる要確認の提案への推定（作業領域。追記専用）。提案の状態・値は変えず、人が解決するときの参考にだけ使う。参照は hr_admin とバッチの実行者本人だけ';
CREATE INDEX proposal_agent_suggestion_proposal_idx ON proposal_agent_suggestion (change_proposal_id);

ALTER TABLE proposal_agent_suggestion ENABLE ROW LEVEL SECURITY;
ALTER TABLE proposal_agent_suggestion FORCE ROW LEVEL SECURITY;

CREATE POLICY proposal_agent_suggestion_select ON proposal_agent_suggestion FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));
CREATE POLICY proposal_agent_suggestion_insert ON proposal_agent_suggestion FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_kind()) = 'agent'
    AND suggested_by_person_id = (SELECT jinji_viewer_person_id())
    AND ((SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id)));

GRANT SELECT, INSERT ON proposal_agent_suggestion TO jinji_app;
