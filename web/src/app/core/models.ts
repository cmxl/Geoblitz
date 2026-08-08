import { InjectionToken, isDevMode } from '@angular/core';

export interface CityHit {
  name: string;
  country: string;
  population: number;
  lat: number;
  lon: number;
  distanceKm: number;
}
export interface CitiesResponse {
  count: number;
  cities: CityHit[];
}
export interface DistanceResponse {
  kilometers: number;
}
export interface ApiProblem {
  title: string;
  status: number;
  detail: string;
}
export class GeoApiError extends Error {
  readonly problem: ApiProblem;
  constructor(problem: ApiProblem) {
    super(problem.detail);
    this.problem = problem;
  }
}
export interface ApiResult<T> {
  data: T;
  engineMicros: number | null;
  computeCount: number | null;
  httpMillis: number;
}
export type QueryMode = 'nearest' | 'within' | 'distance';
export interface RequestTiming {
  engineMicros: number | null;
  httpMillis: number;
  computeCount: number | null;
  cacheHit: boolean;
  at: number;
}
// Dev-server workflow (`ng serve` on :4200) still talks to the API on :5235 via dev-only
// CORS. Production builds are served by the API itself (tools/publish-web.ps1 +
// single-origin hosting in Program.cs), so a relative/same-origin base URL is correct there
// and CORS never enters the picture.
export const GEO_API_BASE_URL = new InjectionToken<string>('GEO_API_BASE_URL', {
  factory: () => (isDevMode() ? 'http://localhost:5235' : ''),
});
