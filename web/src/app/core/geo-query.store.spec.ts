import { TestBed } from '@angular/core/testing';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { GeoQueryStore } from './geo-query.store';
import { GeoApiService } from './geo-api.service';
import { ApiResult, CitiesResponse, GeoApiError } from './models';

function citiesResult(n: number, computeCount: number | null): ApiResult<CitiesResponse> {
  const cities = Array.from({ length: n }, (_, i) => ({
    name: `C${i}`,
    country: 'DE',
    population: 1000,
    lat: 48 + i,
    lon: 11,
    distanceKm: i,
  }));
  return { data: { count: n, cities }, engineMicros: 5, computeCount, httpMillis: 1.5 };
}

describe('GeoQueryStore', () => {
  let api: {
    nearest: ReturnType<typeof vi.fn>;
    within: ReturnType<typeof vi.fn>;
    distance: ReturnType<typeof vi.fn>;
  };
  let store: InstanceType<typeof GeoQueryStore>;

  beforeEach(() => {
    api = { nearest: vi.fn(), within: vi.fn(), distance: vi.fn() };
    TestBed.configureTestingModule({ providers: [{ provide: GeoApiService, useValue: api }] });
    store = TestBed.inject(GeoQueryStore);
  });

  it('clamps parameters to API limits', () => {
    store.setCount(999);
    expect(store.count()).toBe(100);
    store.setCount(0);
    expect(store.count()).toBe(1);
    store.setCount(7.9);
    expect(store.count()).toBe(7);
    store.setRadiusKm(9999);
    expect(store.radiusKm()).toBe(500);
    store.setRadiusKm(-3);
    expect(store.radiusKm()).toBe(0.1);
    store.setMinPopulation(-5);
    expect(store.minPopulation()).toBe(0);
  });

  it('falls back to the current value when a clamp input is not finite', () => {
    store.setCount(42);
    store.setCount(NaN);
    expect(store.count()).toBe(42);
    store.setCount(Infinity);
    expect(store.count()).toBe(42);

    store.setRadiusKm(75);
    store.setRadiusKm(NaN);
    expect(store.radiusKm()).toBe(75);
    store.setRadiusKm(-Infinity);
    expect(store.radiusKm()).toBe(75);

    store.setMinPopulation(1234);
    store.setMinPopulation(NaN);
    expect(store.minPopulation()).toBe(1234);
  });

  it('queryNearest stores results and the query point', async () => {
    api.nearest.mockResolvedValue(citiesResult(3, 1));
    await store.queryNearest(48.1, 11.5);
    expect(api.nearest).toHaveBeenCalledWith(48.1, 11.5, 10);
    expect(store.results().length).toBe(3);
    expect(store.queryPoint()).toEqual({ lat: 48.1, lon: 11.5 });
    expect(store.busy()).toBe(false);
  });

  it('detects cache hits via non-increasing compute count', async () => {
    api.nearest.mockResolvedValueOnce(citiesResult(1, 5));
    await store.queryNearest(1, 1);
    expect(store.lastTiming()!.cacheHit).toBe(false);
    api.nearest.mockResolvedValueOnce(citiesResult(1, 6));
    await store.queryNearest(2, 2);
    expect(store.lastTiming()!.cacheHit).toBe(false);
    api.nearest.mockResolvedValueOnce(citiesResult(1, 5)); // replayed older count
    await store.queryNearest(1, 1);
    expect(store.lastTiming()!.cacheHit).toBe(true);
  });

  it('keeps at most 40 timings, evicting the oldest', async () => {
    for (let i = 1; i <= 45; i++) {
      api.nearest.mockResolvedValueOnce(citiesResult(0, i));
      await store.queryNearest(i, i);
    }
    expect(store.timings().length).toBe(40);
    expect(store.timings()[0].computeCount).toBe(6);
  });

  it('treats a large downward jump in computeCount as a server restart, not a HIT', async () => {
    api.nearest.mockResolvedValueOnce(citiesResult(1, 5));
    await store.queryNearest(1, 1);
    expect(store.lastTiming()!.cacheHit).toBe(false);
    expect(store.maxComputeCount()).toBe(5);

    api.nearest.mockResolvedValueOnce(citiesResult(1, 6));
    await store.queryNearest(2, 2);
    expect(store.lastTiming()!.cacheHit).toBe(false);
    expect(store.maxComputeCount()).toBe(6);

    // Server restarted: counter resets to 1, which is well below half of the previous max (6).
    api.nearest.mockResolvedValueOnce(citiesResult(1, 1));
    await store.queryNearest(3, 3);
    expect(store.lastTiming()!.cacheHit).toBe(false);
    expect(store.maxComputeCount()).toBe(1);
  });

  it('surfaces problem details as inline error and keeps results', async () => {
    api.nearest.mockResolvedValueOnce(citiesResult(2, 1));
    await store.queryNearest(1, 1);
    api.nearest.mockRejectedValueOnce(
      new GeoApiError({ title: 'Invalid request', status: 400, detail: 'radiusKm is required' }),
    );
    await store.queryNearest(2, 2);
    expect(store.error()).toBe('radiusKm is required');
    expect(store.results().length).toBe(2);
    expect(store.linkDown()).toBe(false);
  });

  it('flips linkDown on network errors', async () => {
    api.nearest.mockRejectedValueOnce(new TypeError('fetch failed'));
    await store.queryNearest(1, 1);
    expect(store.linkDown()).toBe(true);
    api.nearest.mockResolvedValueOnce(citiesResult(1, 9));
    await store.queryNearest(1, 1);
    expect(store.linkDown()).toBe(false);
  });

  it('routes a 5xx GeoApiError to linkDown, not the inline error', async () => {
    api.nearest.mockRejectedValueOnce(
      new GeoApiError({
        title: 'Internal error',
        status: 500,
        detail: 'An unexpected error occurred.',
      }),
    );
    await store.queryNearest(1, 1);
    expect(store.linkDown()).toBe(true);
    expect(store.error()).toBeNull();
  });

  it('a sub-500 GeoApiError after an outage clears linkDown and sets the inline error', async () => {
    api.nearest.mockRejectedValueOnce(new TypeError('fetch failed'));
    await store.queryNearest(1, 1);
    expect(store.linkDown()).toBe(true);

    api.nearest.mockRejectedValueOnce(
      new GeoApiError({
        title: 'Invalid request',
        status: 400,
        detail: 'count must be an integer in [1, 100]',
      }),
    );
    await store.queryNearest(1, 1);
    expect(store.linkDown()).toBe(false);
    expect(store.error()).toBe('count must be an integer in [1, 100]');
  });

  it('ruler: two points trigger distance, third restarts', async () => {
    api.distance.mockResolvedValue({
      data: { kilometers: 504.2 },
      engineMicros: 1,
      computeCount: 2,
      httpMillis: 1,
    });
    store.setMode('distance');
    await store.addRulerPoint(52.52, 13.405);
    expect(store.distanceKm()).toBeNull();
    await store.addRulerPoint(48.1374, 11.5755);
    expect(store.distanceKm()).toBeCloseTo(504.2, 5);
    await store.addRulerPoint(50, 10);
    expect(store.rulerPoints().length).toBe(1);
    expect(store.distanceKm()).toBeNull();
  });

  it('setMode clears transient state', async () => {
    api.nearest.mockResolvedValue(citiesResult(2, 1));
    await store.queryNearest(1, 1);
    store.highlight(1);
    store.setMode('within');
    expect(store.results().length).toBe(0);
    expect(store.highlightedIndex()).toBeNull();
    expect(store.error()).toBeNull();
  });

  describe('queryWithin', () => {
    it('calls api.within with lat, lon, and radiusKm/minPopulation from state', async () => {
      store.setRadiusKm(75);
      store.setMinPopulation(50_000);
      api.within.mockResolvedValueOnce(citiesResult(2, 1));
      await store.queryWithin(48.1, 11.5);
      expect(api.within).toHaveBeenCalledWith(48.1, 11.5, 75, 50_000);
    });

    it('clamps an optional radiusKm override before calling api.within', async () => {
      api.within.mockResolvedValueOnce(citiesResult(1, 1));
      await store.queryWithin(48.1, 11.5, 9999);
      expect(store.radiusKm()).toBe(500);
      expect(api.within).toHaveBeenCalledWith(48.1, 11.5, 500, 0);
    });

    it('ignores a non-finite radiusKm override and keeps the current value', async () => {
      store.setRadiusKm(20);
      api.within.mockResolvedValueOnce(citiesResult(1, 1));
      await store.queryWithin(48.1, 11.5, NaN);
      expect(store.radiusKm()).toBe(20);
      expect(api.within).toHaveBeenCalledWith(48.1, 11.5, 20, 0);
    });

    it('applies results and the query point', async () => {
      api.within.mockResolvedValueOnce(citiesResult(4, 1));
      await store.queryWithin(48.1, 11.5);
      expect(store.results().length).toBe(4);
      expect(store.queryPoint()).toEqual({ lat: 48.1, lon: 11.5 });
      expect(store.highlightedIndex()).toBeNull();
    });
  });

  describe('in-flight request sequencing', () => {
    it('discards a stale response when a newer request resolves first', async () => {
      let resolveFirst!: (r: ApiResult<CitiesResponse>) => void;
      let resolveSecond!: (r: ApiResult<CitiesResponse>) => void;
      api.nearest
        .mockImplementationOnce(
          () => new Promise<ApiResult<CitiesResponse>>((res) => (resolveFirst = res)),
        )
        .mockImplementationOnce(
          () => new Promise<ApiResult<CitiesResponse>>((res) => (resolveSecond = res)),
        );

      const first = store.queryNearest(1, 1);
      const second = store.queryNearest(2, 2);

      // Newer request (second) resolves first; older (first) resolves after.
      resolveSecond(citiesResult(2, 20));
      await second;
      resolveFirst(citiesResult(1, 10));
      await first;

      expect(store.results().length).toBe(2);
      expect(store.queryPoint()).toEqual({ lat: 2, lon: 2 });
      expect(store.lastTiming()!.computeCount).toBe(20);
      expect(store.timings().length).toBe(1);
      expect(store.busy()).toBe(false);
    });
  });
});
