import { InjectionToken } from '@angular/core';

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
export const GEO_API_BASE_URL = new InjectionToken<string>('GEO_API_BASE_URL', {
  factory: () => 'http://localhost:5235',
});
