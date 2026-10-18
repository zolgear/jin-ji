// OpenAPI 3.1 の文書（ASP.NET Core の組み込み生成器の出力）から、API クライアントの型を生成する。
//
//   node scripts/gen-api-types.mjs <openapi.json> <出力 .ts>
//   node scripts/gen-api-types.mjs <openapi.json> <出力 .ts> --check   # 生成結果と出力ファイルが一致するかだけを確かめる
//
// openapi-typescript は TypeScript の JS API（7.0 のネイティブ版には無い）と多数の推移的依存を要するため使わず、
// jin-ji の API が使う構文（$ref・oneOf・type の配列・required・items・additionalProperties・enum）だけを扱う小さな変換にした。
// 扱えない構文に出会ったら黙って unknown にせず失敗させる（API 側の変化に気付けるように）。
import { readFileSync, writeFileSync } from "node:fs";

const SUPPORTED_KEYS = new Set([
  "type", "format", "properties", "required", "items", "$ref", "oneOf", "enum", "pattern",
  "description", "additionalProperties", "nullable", "default",
]);

/** スキーマを TypeScript の型の式に変換する。 */
export function schemaToTs(schema, indent = "") {
  if (schema === true || schema === undefined || (schema && Object.keys(schema).length === 0)) {
    return "unknown";
  }
  for (const key of Object.keys(schema)) {
    if (!SUPPORTED_KEYS.has(key)) {
      throw new Error(`未対応のスキーマのキー: ${key}`);
    }
  }
  if (schema.$ref) {
    const prefix = "#/components/schemas/";
    if (!schema.$ref.startsWith(prefix)) {
      throw new Error(`未対応の $ref: ${schema.$ref}`);
    }
    return `components["schemas"][${JSON.stringify(schema.$ref.slice(prefix.length))}]`;
  }
  if (schema.oneOf) {
    return union(schema.oneOf.map((s) => schemaToTs(s, indent)));
  }
  if (schema.enum) {
    return union(schema.enum.map((v) => JSON.stringify(v)));
  }
  const types = Array.isArray(schema.type) ? schema.type : schema.type ? [schema.type] : [];
  if (types.length === 0) {
    if (schema.properties) {
      return objectToTs(schema, indent);
    }
    return "unknown";
  }
  return union(types.map((t) => {
    switch (t) {
      case "null": return "null";
      case "string": return "string";
      case "integer":
      case "number": return "number";
      case "boolean": return "boolean";
      case "array": return `${wrap(schemaToTs(schema.items, indent))}[]`;
      case "object": return objectToTs(schema, indent);
      default: throw new Error(`未対応の type: ${t}`);
    }
  }));
}

function wrap(type) {
  return /[|&]/.test(type) ? `(${type})` : type;
}

function union(parts) {
  const unique = [...new Set(parts)];
  // null は最後に置く（読みやすさのため）
  unique.sort((a, b) => (a === "null") - (b === "null"));
  return unique.join(" | ");
}

function objectToTs(schema, indent) {
  const inner = `${indent}  `;
  const required = new Set(schema.required ?? []);
  const lines = [];
  for (const [name, prop] of Object.entries(schema.properties ?? {})) {
    const doc = prop.description ? `${inner}/** ${prop.description.replace(/\*\//g, "* /")} */\n` : "";
    const format = prop.format ? ` // ${prop.format}` : "";
    lines.push(`${doc}${inner}${safeKey(name)}${required.has(name) ? "" : "?"}: ${schemaToTs(prop, inner)};${format}`);
  }
  if (schema.additionalProperties && schema.additionalProperties !== false) {
    lines.push(`${inner}[key: string]: ${schemaToTs(schema.additionalProperties === true ? {} : schema.additionalProperties, inner)};`);
  }
  if (lines.length === 0) {
    return "Record<string, never>";
  }
  return `{\n${lines.join("\n")}\n${indent}}`;
}

function safeKey(name) {
  return /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(name) ? name : JSON.stringify(name);
}

function parametersToTs(parameters, location, indent) {
  const list = (parameters ?? []).filter((p) => p.in === location);
  if (list.length === 0) {
    return "Record<string, never>";
  }
  const inner = `${indent}  `;
  const lines = list.map((p) => `${inner}${safeKey(p.name)}${p.required ? "" : "?"}: ${schemaToTs(p.schema ?? {}, inner)};`);
  return `{\n${lines.join("\n")}\n${indent}}`;
}

function responseToTs(operation, indent) {
  const ok = operation.responses?.["200"] ?? operation.responses?.["201"];
  const content = ok?.content;
  if (!content) {
    return "void";
  }
  const json = content["application/json"];
  if (json?.schema) {
    return schemaToTs(json.schema, indent);
  }
  // JSON 以外（ZIP・テキスト）はバイナリとして扱う
  return "Blob";
}

/** OpenAPI の文書から型定義のソースを生成する。 */
export function generate(document) {
  if (!String(document.openapi ?? "").startsWith("3.1")) {
    throw new Error(`OpenAPI 3.1 の文書ではありません: ${document.openapi}`);
  }
  const out = [];
  out.push("// このファイルは scripts/gen-api-types.mjs が openapi/v1.json から生成する。手で編集しない（pnpm gen:api で再生成）。");
  out.push(`// ${document.info?.title ?? ""} ${document.info?.version ?? ""}`.trimEnd());
  out.push("/* eslint-disable */");
  out.push("");
  out.push("export interface components {");
  out.push("  schemas: {");
  const schemas = document.components?.schemas ?? {};
  for (const name of Object.keys(schemas).sort()) {
    const schema = schemas[name];
    if (schema.description) {
      out.push(`    /** ${schema.description} */`);
    }
    out.push(`    ${safeKey(name)}: ${schemaToTs(schema, "    ")};`);
  }
  out.push("  };");
  out.push("}");
  out.push("");
  out.push("export interface paths {");
  for (const path of Object.keys(document.paths ?? {}).sort()) {
    out.push(`  ${JSON.stringify(path)}: {`);
    const item = document.paths[path];
    for (const method of ["get", "post", "put", "patch", "delete"]) {
      const operation = item[method];
      if (!operation) continue;
      const summary = operation.summary ? `    /** ${operation.summary}${operation.operationId ? `（${operation.operationId}）` : ""} */\n` : "";
      const body = operation.requestBody?.content?.["application/json"]?.schema;
      out.push(`${summary}    ${method}: {`);
      out.push(`      path: ${parametersToTs(operation.parameters, "path", "      ")};`);
      out.push(`      query: ${parametersToTs(operation.parameters, "query", "      ")};`);
      out.push(`      body: ${body ? schemaToTs(body, "      ") : "never"};`);
      out.push(`      response: ${responseToTs(operation, "      ")};`);
      out.push("    };");
    }
    out.push("  };");
  }
  out.push("}");
  out.push("");
  out.push("export type Schemas = components[\"schemas\"];");
  out.push("");
  return out.join("\n");
}

const isMain = process.argv[1] && import.meta.url === new URL(`file://${process.argv[1]}`).href;
if (isMain) {
  const [input, output, flag] = process.argv.slice(2);
  if (!input || !output) {
    console.error("使い方: node scripts/gen-api-types.mjs <openapi.json> <出力 .ts> [--check]");
    process.exit(2);
  }
  const source = generate(JSON.parse(readFileSync(input, "utf8")));
  if (flag === "--check") {
    const current = readFileSync(output, "utf8");
    if (current !== source) {
      console.error(`${output} が ${input} と一致しません。pnpm gen:api で再生成してください。`);
      process.exit(1);
    }
    console.log(`${output} は最新です。`);
  } else {
    writeFileSync(output, source);
    console.log(`${output} を生成しました。`);
  }
}
