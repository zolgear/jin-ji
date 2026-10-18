-- 0010: 要確認の解決を 2 人体制にする（取込の実行者が解決してもよい）
--
-- 0008 では「取込の実行者・解決者・承認者」の 3 人が揃わないと、要確認だった変更が公開側に出なかった。
-- 少人数の人事部門では負担が大きいため、取込の実行者が解決（または却下）まで行い、承認だけを別の人が行う 2 人体制にする。
--   - 解決者 ≠ 起案者（取込の実行者）の制約を外す
--   - 職務分掌の CHECK（判断者 ≠ 起案者）は、公開側を変える「反映（applied）」に限る。
--     解決（resolved）・却下（rejected）は公開側を変えないので、起案者本人が行ってもよい
--   - 承認者 ≠ 起案者（反映時）と、承認者 ≠ 解決者（change_proposal_approver_not_resolver）は残す
--     → 一人で公開側を変えることはできない（職務分掌 §5.5）

ALTER TABLE change_proposal DROP CONSTRAINT change_proposal_resolver_not_proposer;

ALTER TABLE change_proposal DROP CONSTRAINT change_proposal_segregation_of_duties;
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_segregation_of_duties CHECK (
    status <> 'applied' OR decided_by_person_id IS DISTINCT FROM proposed_by_person_id);

COMMENT ON COLUMN change_proposal.resolved_by_person_id IS '解決の結果の提案なら、解決した人（hr_admin。取込の実行者でもよい。承認者とは別の人）';
COMMENT ON TABLE change_proposal IS '変更提案。取込結果と現在のデータの差分・根拠（取込元の行）・確信度を持つ。起案者（取込の実行者）とも解決者とも別の hr_admin が承認したときだけ公開側に反映する';
