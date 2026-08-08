import { TestBed } from '@angular/core/testing';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { GeoApiService } from './geo-api.service';
import { GeoApiError } from './models';

function jsonResponse(body: unknown, headers: Record<string, string> = {}, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'content-type': status >= 400 ? 'application/problem+json' : 'application/json',
      ...headers,
    },
  });
}

describe('GeoApiService', () => {
  let service: GeoApiService;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    service = TestBed.inject(GeoApiService);
  });
  afterEach(() => vi.unstubAllGlobals());

  it('builds the nearest URL with all params', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ count: 0, cities: [] }));
    await service.nearest(48.1374, 11.5755, 10);
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:5235/cities/nearest?lat=48.1374&lon=11.5755&count=10',
    );
  });

  it('builds the within URL including minPopulation', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ count: 0, cities: [] }));
    await service.within(48, 11, 25, 100000);
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:5235/cities/within?lat=48&lon=11&radiusKm=25&minPopulation=100000',
    );
  });

  it('parses Server-Timing into microseconds and X-Compute-Count into a number', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(
        { count: 0, cities: [] },
        { 'Server-Timing': 'engine;dur=0.009', 'X-Compute-Count': '42' },
      ),
    );
    const r = await service.nearest(1, 2, 3);
    expect(r.engineMicros).toBeCloseTo(9, 5);
    expect(r.computeCount).toBe(42);
    expect(r.httpMillis).toBeGreaterThanOrEqual(0);
  });

  it('builds the distance URL with all params in order', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ kilometers: 0 }));
    await service.distance(52.52, 13.405, 48.1374, 11.5755);
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:5235/distance?fromLat=52.52&fromLon=13.405&toLat=48.1374&toLon=11.5755',
    );
  });

  it('returns nulls when timing headers are absent', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ kilometers: 504.2 }));
    const r = await service.distance(52.52, 13.405, 48.1374, 11.5755);
    expect(r.engineMicros).toBeNull();
    expect(r.computeCount).toBeNull();
    expect(r.data.kilometers).toBeCloseTo(504.2, 5);
  });

  it('returns null engine micros for an unparseable Server-Timing header', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({ count: 0, cities: [] }, { 'Server-Timing': 'engine;dur=abc' }),
    );
    const r = await service.nearest(1, 2, 3);
    expect(r.engineMicros).toBeNull();
  });

  it('throws GeoApiError with problem details on 400', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(
        { title: 'Invalid request', status: 400, detail: 'count must be an integer in [1, 100]' },
        {},
        400,
      ),
    );
    await expect(service.nearest(1, 2, 999)).rejects.toSatisfy(
      (e: unknown) =>
        e instanceof GeoApiError && e.problem.status === 400 && e.problem.detail.includes('count'),
    );
  });

  it('throws a plain error for non-problem failures', async () => {
    fetchMock.mockResolvedValue(new Response('boom', { status: 502 }));
    await expect(service.nearest(1, 2, 3)).rejects.toThrow('geo-api: HTTP 502');
  });
});
