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

export const GeoQueryStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withComputed(({ timings }) => ({
    lastTiming: computed<RequestTiming | null>(() => timings().at(-1) ?? null),
  })),
  withMethods((store, api = inject(GeoApiService)) => {
    function pushTiming(r: ApiResult<unknown>): void {
      const cacheHit = r.computeCount !== null && r.computeCount <= store.maxComputeCount();
      const timing: RequestTiming = {
        engineMicros: r.engineMicros,
        httpMillis: r.httpMillis,
        computeCount: r.computeCount,
        cacheHit,
        at: Date.now(),
      };
      patchState(store, {
        timings: [...store.timings(), timing].slice(-MAX_TIMINGS),
        maxComputeCount: cacheHit
          ? store.maxComputeCount()
          : Math.max(store.maxComputeCount(), r.computeCount ?? 0),
      });
    }

    async function run<T>(
      call: () => Promise<ApiResult<T>>,
      apply: (r: ApiResult<T>) => void,
    ): Promise<void> {
      patchState(store, { busy: true, error: null });
      try {
        const r = await call();
        apply(r);
        pushTiming(r);
        patchState(store, { linkDown: false });
      } catch (e) {
        if (e instanceof GeoApiError) patchState(store, { error: e.problem.detail });
        else patchState(store, { linkDown: true });
      } finally {
        patchState(store, { busy: false });
      }
    }

    return {
      setMode(mode: QueryMode): void {
        patchState(store, {
          mode,
          results: [],
          distanceKm: null,
          rulerPoints: [],
          error: null,
          highlightedIndex: null,
        });
      },
      setCount(n: number): void {
        patchState(store, { count: Math.min(100, Math.max(1, Math.trunc(n))) });
      },
      setRadiusKm(n: number): void {
        patchState(store, { radiusKm: Math.min(500, Math.max(0.1, n)) });
      },
      setMinPopulation(n: number): void {
        patchState(store, { minPopulation: Math.max(0, Math.trunc(n)) });
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
        if (radiusKm !== undefined)
          patchState(store, { radiusKm: Math.min(500, Math.max(0.1, radiusKm)) });
        patchState(store, { queryPoint: { lat, lon } });
        return run(
          () => api.within(lat, lon, store.radiusKm(), store.minPopulation()),
          (r) => patchState(store, { results: r.data.cities, highlightedIndex: null }),
        );
      },
      async addRulerPoint(lat: number, lon: number): Promise<void> {
        const points = store.rulerPoints();
        if (points.length >= 2) {
          patchState(store, { rulerPoints: [{ lat, lon }], distanceKm: null });
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
