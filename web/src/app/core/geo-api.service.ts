import { Injectable, inject } from '@angular/core';
import {
  ApiResult,
  CitiesResponse,
  DistanceResponse,
  GEO_API_BASE_URL,
  GeoApiError,
} from './models';

@Injectable({ providedIn: 'root' })
export class GeoApiService {
  private readonly baseUrl = inject(GEO_API_BASE_URL);

  nearest(lat: number, lon: number, count: number): Promise<ApiResult<CitiesResponse>> {
    return this.get<CitiesResponse>('/cities/nearest', { lat, lon, count });
  }

  within(
    lat: number,
    lon: number,
    radiusKm: number,
    minPopulation: number,
  ): Promise<ApiResult<CitiesResponse>> {
    return this.get<CitiesResponse>('/cities/within', { lat, lon, radiusKm, minPopulation });
  }

  distance(
    fromLat: number,
    fromLon: number,
    toLat: number,
    toLon: number,
  ): Promise<ApiResult<DistanceResponse>> {
    return this.get<DistanceResponse>('/distance', { fromLat, fromLon, toLat, toLon });
  }

  private async get<T>(path: string, params: Record<string, number>): Promise<ApiResult<T>> {
    const query = Object.entries(params)
      .map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`)
      .join('&');
    const started = performance.now();
    const res = await fetch(`${this.baseUrl}${path}?${query}`);

    if (!res.ok) {
      if (res.headers.get('content-type')?.includes('application/problem+json')) {
        throw new GeoApiError(await res.json());
      }
      throw new Error(`geo-api: HTTP ${res.status}`);
    }

    const engineMicros = parseEngineMicros(res.headers.get('Server-Timing'));
    const computeCount = parseCount(res.headers.get('X-Compute-Count'));
    const data = (await res.json()) as T;
    // Measured after the body is fully consumed so httpMillis reflects the full round trip
    // (network + Kestrel + serialization + download), not just the time to response headers.
    const httpMillis = performance.now() - started;

    return { data, engineMicros, computeCount, httpMillis };
  }
}

function parseEngineMicros(header: string | null): number | null {
  const match = header?.match(/engine;dur=([0-9.]+)/);
  if (!match) return null;
  const micros = Number(match[1]) * 1000;
  return Number.isFinite(micros) ? micros : null;
}

function parseCount(header: string | null): number | null {
  if (header === null) return null;
  const n = Number(header);
  return Number.isFinite(n) ? n : null;
}
