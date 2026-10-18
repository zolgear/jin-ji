namespace JinJi.Domain;

/// <summary>
/// エンティティ ID の生成。ID は UUIDv7（時刻順に並ぶ）をアプリ側で生成する。
/// PostgreSQL 17 には uuidv7() が無いため、DB 側の既定値には頼らない。
/// ID は秘密ではない。推測されても認可で守る（アーキテクチャ概要 §7）。
/// </summary>
public static class EntityId
{
    /// <summary>新しい UUIDv7 を返す。</summary>
    public static Guid New() => Guid.CreateVersion7();

    /// <summary>指定時刻を埋め込んだ UUIDv7 を返す（取込データの順序を保つ場合などに使う）。</summary>
    public static Guid New(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp);
}
