import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { GeoApiService } from './geo-api.service';
import { ApiResult, CityHit, GeoApiError, QueryMode, RequestTiming } from './models';

interface GeoQueryState {
  mode: QueryMode;
  count: number;
  radiusKm: number;
  minPopulation: number;
  queryPoint: { lat: number; lon: number } | null;
  rulerPoints: { lat: number; lon: number }[];
  results: CityHit[];
  distanceKm: number | null;
  timings: RequestTiming[];
  maxComputeCount: number;
  error: string | null;
  busy: boolean;
  linkDown: boolean;
  highlightedIndex: number | null;
}

const initialState: GeoQueryState = {
  mode: 'nearest',
  count: 10,
  radiusKm: 50,
  minPopulation: 0,
  queryPoint: null,
  rulerPoints: [],
  results: [],
  distanceKm: null,
  timings: [],
  maxComputeCount: 0,
  error: null,
  busy: false,
  linkDown: false,
  highlightedIndex: null,
};

const MAX_TIMINGS = 40;

/** Clamps `n` to `[min, max]`. Callers apply `Math.trunc` themselves for integer fields. */
function clamp(n: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, n));
}

export const GeoQueryStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withComputed(({ timings }) => ({
    lastTiming: computed<RequestTiming | null>(() => timings().at(-1) ?? null),
  })),
  withMethods((store, api = inject(GeoApiService)) => {
    // Restart-safe cache-hit heuristic.
    //
    // A HIT replays the exact computeCount recorded when an entry was cached, which can lag
    // behind maxComputeCount because other (unrelated) cache misses advance the counter in the
    // meantime. We accept any computeCount <= maxComputeCount within a margin of 10_000 as a HIT.
    //
    // A dev API restart resets the server-side counter to a small number, which would otherwise
    // satisfy computeCount <= maxComputeCount forever, reporting HIT permanently (MINOR-3). We
    // treat a computeCount at or below half of maxComputeCount as "probably a restart, not a
    // cache hit" and reset maxComputeCount to it. The boundary (cc === max * 0.5) counts as a
    // restart, not a HIT — with the opposite convention a restart landing exactly on that
    // boundary would freeze maxComputeCount and report up to `max - cc` further false HITs
    // before self-healing.
    //
    // Trade-off: a legitimately very old cached entry whose computeCount is at or below half of
    // the current max is misreported as a restart/MISS instead of a HIT. That's judged safer
    // than reporting HIT forever after a real restart.
    function pushTiming(r: ApiResult<unknown>): void {
      const max = store.maxComputeCount();
      const cc = r.computeCount;
      const isRestart = cc !== null && cc <= max * 0.5;
      const isHit = !isRestart && cc !== null && cc <= max && max - cc <= 10_000;
      const timing: RequestTiming = {
        engineMicros: r.engineMicros,
        httpMillis: r.httpMillis,
        computeCount: cc,
        cacheHit: isHit,
        at: Date.now(),
      };
      patchState(store, {
        timings: [...store.timings(), timing].slice(-MAX_TIMINGS),
        maxComputeCount: isRestart ? (cc as number) : isHit ? max : Math.max(max, cc ?? 0),
      });
    }

    // Per-store monotonically increasing sequence number. A response only applies its results
    // (and timing/error/linkDown side effects) if it is still the latest in-flight request when
    // it resolves; stale out-of-order responses are discarded entirely.
    let requestSeq = 0;

    async function run<T>(
      call: () => Promise<ApiResult<T>>,
      apply: (r: ApiResult<T>) => void,
    ): Promise<void> {
      const seq = ++requestSeq;
      patchState(store, { busy: true, error: null });
      try {
        const r = await call();
        if (seq !== requestSeq) return; // superseded by a newer request; discard
        apply(r);
        pushTiming(r);
        patchState(store, { linkDown: false });
      } catch (e) {
        if (seq !== requestSeq) return; // superseded by a newer request; discard
        if (e instanceof GeoApiError) {
          if (e.problem.status >= 500) {
            // Server error: the link itself is fine, but the server is failing hard — treat like
            // a connectivity problem rather than a user-actionable inline error.
            patchState(store, { linkDown: true });
          } else {
            // Client error (4xx): the round trip succeeded, so the link is proven up.
            patchState(store, { error: e.problem.detail, linkDown: false });
          }
        } else {
          patchState(store, { linkDown: true });
        }
      } finally {
        if (seq === requestSeq) patchState(store, { busy: false });
      }
    }

    function setRadiusKm(n: number): void {
      const current = store.radiusKm();
      patchState(store, { radiusKm: Number.isFinite(n) ? clamp(n, 0.1, 500) : current });
    }

    return {
      setMode(mode: QueryMode): void {
        requestSeq++; // stale in-flight responses from the previous mode must not land here
        patchState(store, {
          mode,
          results: [],
          distanceKm: null,
          rulerPoints: [],
          error: null,
          highlightedIndex: null,
          // The bump above means no in-flight request's `finally` will ever see its seq match
          // requestSeq again, so busy would otherwise stay stuck true forever (MINOR-1).
          busy: false,
        });
      },
      setCount(n: number): void {
        const current = store.count();
        patchState(store, {
          count: Number.isFinite(n) ? clamp(Math.trunc(n), 1, 100) : current,
        });
      },
      setRadiusKm,
      setMinPopulation(n: number): void {
        const current = store.minPopulation();
        patchState(store, {
          minPopulation: Number.isFinite(n) ? Math.max(0, Math.trunc(n)) : current,
        });
      },
      highlight(index: number | null): void {
        patchState(store, { highlightedIndex: index });
      },
      clearError(): void {
        patchState(store, { error: null });
      },
      queryNearest(lat: number, lon: number): Promise<void> {
        patchState(store, { queryPoint: { lat, lon } });
        return run(
          () => api.nearest(lat, lon, store.count()),
          (r) => patchState(store, { results: r.data.cities, highlightedIndex: null }),
        );
      },
      queryWithin(lat: number, lon: number, radiusKm?: number): Promise<void> {
        if (radiusKm !== undefined) setRadiusKm(radiusKm);
        patchState(store, { queryPoint: { lat, lon } });
        return run(
          () => api.within(lat, lon, store.radiusKm(), store.minPopulation()),
          (r) => patchState(store, { results: r.data.cities, highlightedIndex: null }),
        );
      },
      async addRulerPoint(lat: number, lon: number): Promise<void> {
        const points = store.rulerPoints();
        if (points.length >= 2) {
          // Restarting the measurement: bump the sequence so an in-flight /distance response
          // from the just-abandoned pair cannot land and set distanceKm for the new one.
          requestSeq++;
          patchState(store, { rulerPoints: [{ lat, lon }], distanceKm: null, busy: false });
          return;
        }
        const next = [...points, { lat, lon }];
        patchState(store, { rulerPoints: next });
        if (next.length === 2) {
          await run(
            () => api.distance(next[0].lat, next[0].lon, next[1].lat, next[1].lon),
            (r) => patchState(store, { distanceKm: r.data.kilometers }),
          );
        }
      },
    };
  }),
);
