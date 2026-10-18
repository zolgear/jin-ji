// 配置の変化（時点の切り替え・開閉）を補間する。prefers-reduced-motion なら補間しない。
import { animate, useReducedMotion } from "motion/react";
import { useEffect, useRef, useState } from "react";

export interface TweenItem<T> {
  id: string;
  x: number;
  y: number;
  /** 大きさ（区画の枠のように大きさが変わるもの）。あれば位置と同じく補間する。 */
  w?: number;
  h?: number;
  data: T;
}

export interface TweenFrame<T> extends TweenItem<T> {
  opacity: number;
  /** 消えていく途中か（クリックを受けない）。 */
  exiting: boolean;
}

const DURATION = 0.65;

export function useTween<T>(target: TweenItem<T>[], parentOf: (id: string) => string | null): TweenFrame<T>[] {
  const reduced = useReducedMotion();
  const [frames, setFrames] = useState<TweenFrame<T>[]>(() => target.map((t) => ({ ...t, opacity: 1, exiting: false })));
  const current = useRef<TweenFrame<T>[]>(frames);
  const parentRef = useRef(parentOf);
  parentRef.current = parentOf;

  useEffect(() => {
    const before = new Map(current.current.filter((f) => !f.exiting).map((f) => [f.id, f]));
    const after = new Map(target.map((t) => [t.id, t]));
    const finalFrames = target.map((t) => ({ ...t, opacity: 1, exiting: false }));
    if (reduced || before.size === 0) {
      current.current = finalFrames;
      setFrames(finalFrames);
      return;
    }
    // 新しく現れるノードは、前の配置にある最も近い祖先の位置から出す
    type Box = { x: number; y: number; w?: number; h?: number };
    const origin = (id: string): Box => {
      let p = parentRef.current(id);
      while (p) {
        const b = before.get(p);
        if (b) return b;
        p = parentRef.current(p);
      }
      const t = after.get(id)!;
      return { x: t.x, y: t.y };
    };
    // 消えるノードは、新しい配置にある最も近い祖先の位置へ寄せる
    const destination = (id: string): Box => {
      let p = parentRef.current(id);
      while (p) {
        const a = after.get(p);
        if (a) return a;
        p = parentRef.current(p);
      }
      return before.get(id)!;
    };
    const exiting = [...before.values()].filter((b) => !after.has(b.id));
    const plans = [
      ...target.map((t) => {
        const from = before.get(t.id) ?? origin(t.id);
        return { item: t, from: from as Box, to: t as Box, op: before.has(t.id) ? [1, 1] : [0, 1], exiting: false };
      }),
      ...exiting.map((e) => ({ item: e, from: e as Box, to: { ...e, ...destination(e.id), w: e.w, h: e.h } as Box, op: [1, 0], exiting: true })),
    ];
    const ease = (t: number) => 1 - Math.pow(1 - t, 3);
    const lerp = (a: number, b: number, t: number) => a + (b - a) * t;
    const controls = animate(0, 1, {
      duration: DURATION,
      ease: "linear",
      onUpdate: (raw) => {
        const t = ease(raw);
        const next = plans.map((p) => ({
          id: p.item.id,
          data: p.item.data,
          x: p.from.x + (p.to.x - p.from.x) * t,
          y: p.from.y + (p.to.y - p.from.y) * t,
          ...(p.to.w !== undefined && p.to.h !== undefined
            ? { w: lerp(p.from.w ?? p.to.w, p.to.w, t), h: lerp(p.from.h ?? p.to.h, p.to.h, t) }
            : {}),
          opacity: p.op[0]! + (p.op[1]! - p.op[0]!) * t,
          exiting: p.exiting,
        }));
        current.current = next;
        setFrames(next);
      },
      onComplete: () => {
        current.current = finalFrames;
        setFrames(finalFrames);
      },
    });
    return () => controls.stop();
    // target の参照が変わったときだけ動かす
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [target, reduced]);

  return frames;
}
