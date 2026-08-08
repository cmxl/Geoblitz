import * as L from 'leaflet';
import { CityHit } from '../core/models';

const CYAN = '#54d7ff';
const AMBER = '#ffd166';

export function buildQueryPin(lat: number, lon: number): L.CircleMarker {
  return L.circleMarker([lat, lon], {
    radius: 7,
    color: CYAN,
    weight: 2,
    fillColor: CYAN,
    fillOpacity: 0.5,
  });
}

export function buildResultPin(
  city: CityHit,
  index: number,
  highlighted: boolean,
  onHover?: (index: number | null) => void,
): L.CircleMarker {
  const pin = L.circleMarker([city.lat, city.lon], {
    radius: highlighted ? 8 : 5,
    color: AMBER,
    weight: highlighted ? 3 : 1.5,
    fillColor: AMBER,
    fillOpacity: highlighted ? 0.8 : 0.5,
  });
  pin.bindTooltip(`${city.name} · ${city.distanceKm.toFixed(3)} km`, { direction: 'top' });
  if (onHover) {
    pin.on('mouseover', () => onHover(index));
    pin.on('mouseout', () => onHover(null));
  }
  return pin;
}

export function buildRadiusCircle(lat: number, lon: number, radiusKm: number): L.Circle {
  return L.circle([lat, lon], {
    radius: radiusKm * 1000,
    color: CYAN,
    weight: 1.5,
    dashArray: '6 5',
    fillColor: CYAN,
    fillOpacity: 0.06,
  });
}

export function buildRulerLine(points: { lat: number; lon: number }[]): L.Polyline {
  return L.polyline(
    points.map((p) => [p.lat, p.lon] as [number, number]),
    {
      color: AMBER,
      weight: 2,
      dashArray: '8 6',
    },
  );
}

export function distanceLabelHtml(km: number): string {
  return `<span class="fd-distance-label">${km.toFixed(3)} km</span>`;
}
