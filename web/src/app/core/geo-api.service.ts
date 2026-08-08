// TEMPORARY CONTRACT STUB — Task 3 (models.ts / geo-api.service.ts) is being implemented
// concurrently on another branch. This stub exists only so Task 4 (GeoQueryStore) can compile
// and its spec can mock GeoApiService. Contains NO logic. Replace with Task 3's real
// implementation at merge time.
import { Injectable } from '@angular/core';
import { ApiResult, CitiesResponse, DistanceResponse } from './models';

@Injectable({ providedIn: 'root' })
export class GeoApiService {
  nearest(lat: number, lon: number, count: number): Promise<ApiResult<CitiesResponse>> {
    throw new Error('stub');
  }

  within(
    lat: number,
    lon: number,
    radiusKm: number,
    minPopulation: number,
  ): Promise<ApiResult<CitiesResponse>> {
    throw new Error('stub');
  }

  distance(
    fromLat: number,
    fromLon: number,
    toLat: number,
    toLon: number,
  ): Promise<ApiResult<DistanceResponse>> {
    throw new Error('stub');
  }
}
