import { describe, it, expect } from 'vitest';
import {
  buildQueryPin,
  buildResultPin,
  buildRadiusCircle,
  buildRulerLine,
  distanceLabelHtml,
} from './layers';

const city = {
  name: 'München',
  country: 'DE',
  population: 1,
  lat: 48.137,
  lon: 11.575,
  distanceKm: 0.612,
};

describe('layer builders', () => {
  it('query pin is cyan at the queried location', () => {
    const pin = buildQueryPin(48.1, 11.5);
    expect(pin.getLatLng()).toMatchObject({ lat: 48.1, lng: 11.5 });
    expect(pin.options.color).toBe('#54d7ff');
  });

  it('result pin grows and brightens when highlighted', () => {
    expect(buildResultPin(city, 0, false).options.radius).toBe(5);
    expect(buildResultPin(city, 0, true).options.radius).toBe(8);
    expect(buildResultPin(city, 0, false).options.color).toBe('#ffd166');
  });

  it('result pin tooltip carries name and distance', () => {
    const tooltip = buildResultPin(city, 0, false).getTooltip();
    expect(tooltip!.getContent()).toBe('München · 0.612 km');
  });

  it('escapes HTML-significant characters in the city name before it reaches the tooltip innerHTML sink', () => {
    const malicious = { ...city, name: '<b>x</b>' };
    const tooltip = buildResultPin(malicious, 0, false).getTooltip();
    expect(tooltip!.getContent()).toBe('&lt;b&gt;x&lt;/b&gt; · 0.612 km');
    expect(tooltip!.getContent()).not.toContain('<b>');
  });

  it('radius circle converts km to meters', () => {
    expect(buildRadiusCircle(48, 11, 25).getRadius()).toBe(25000);
  });

  it('ruler line follows the points', () => {
    const line = buildRulerLine([
      { lat: 1, lon: 2 },
      { lat: 3, lon: 4 },
    ]);
    expect(line.getLatLngs()).toHaveLength(2);
  });

  it('distance label renders formatted km', () => {
    expect(distanceLabelHtml(504.2)).toContain('504.200 km');
  });
});
