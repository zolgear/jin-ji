# 3D 俯瞰のテクスチャ

3D 俯瞰（`../Overview3D.tsx`）のモジュールに使うグレースケールのタイル。色はマテリアル側で付け、ライト・ダークの両方で使い回す。
three の `MirroredRepeatWrapping`（鏡像の敷き詰め）で継ぎ目を消し、ミップマップを使う。読み込めない環境では使わずに、形と色だけで描く。

出所: 画像生成 AI（gpt-image-2、quality=low・1024x1024）で生成し、縁を 6px 切って縮小・コントラストを正規化して WebP に圧縮した。

| ファイル | 大きさ | 使い方 | 生成のプロンプト（要約） |
| --- | --- | --- | --- |
| `panel.webp` | 512px・約 40KB | 外装の凹凸（bumpMap） | Seamless tileable texture, top-down orthographic, industrial machine panel surface: a square grid of thin recessed panel seams dividing the image into four equal square panels, tiny countersunk screws near each panel corner, subtle fine matte grain, uniform even lighting, grayscale |
| `hairline.webp` | 512px・約 74KB | 外装の粗さ（roughnessMap） | Seamless tileable texture, top-down orthographic, brushed aluminum hairline finish with fine straight horizontal brush lines, uniform even lighting, grayscale |
| `bead.webp` | 256px・約 28KB | 上面の板の微細な凹凸（bumpMap） | Seamless tileable texture, top-down orthographic, bead-blasted matte industrial plastic surface with very fine uniform micro stipple grain, uniform even lighting, grayscale |
