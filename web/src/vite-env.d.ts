/// <reference types="vite/client" />

/** デモアダプタを含むビルドか（vite.config.ts の define）。本番ビルドでは false の定数。 */
declare const __JINJI_DEMO__: boolean;

/** jin-ji の版（web/package.json の version。vite.config.ts の define）。 */
declare const __JINJI_VERSION__: string;
