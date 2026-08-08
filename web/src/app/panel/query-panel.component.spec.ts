import { TestBed } from '@angular/core/testing';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { QueryPanelComponent } from './query-panel.component';
import { GeoQueryStore } from '../core/geo-query.store';
import { GeoApiService } from '../core/geo-api.service';

describe('QueryPanelComponent', () => {
  let api: { nearest: ReturnType<typeof vi.fn>; within: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    api = { nearest: vi.fn(), within: vi.fn() };
    TestBed.configureTestingModule({ providers: [{ provide: GeoApiService, useValue: api }] });
  });

  function render() {
    const fixture = TestBed.createComponent(QueryPanelComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('switches store mode from the mode buttons', () => {
    const fixture = render();
    const store = TestBed.inject(GeoQueryStore);
    const buttons = fixture.nativeElement.querySelectorAll('button.mode');
    expect(buttons.length).toBe(3);
    (buttons[1] as HTMLButtonElement).click();
    expect(store.mode()).toBe('within');
  });

  it('renders results with rank, country and distance', async () => {
    api.nearest.mockResolvedValue({
      data: {
        count: 2,
        cities: [
          {
            name: 'München',
            country: 'DE',
            population: 1471508,
            lat: 48.13,
            lon: 11.57,
            distanceKm: 0.612,
          },
          {
            name: 'Pasing',
            country: 'DE',
            population: 40000,
            lat: 48.14,
            lon: 11.46,
            distanceKm: 7.918,
          },
        ],
      },
      engineMicros: 5,
      computeCount: 1,
      httpMillis: 1,
    });
    await TestBed.inject(GeoQueryStore).queryNearest(48.137, 11.576);
    const el = render().nativeElement as HTMLElement;
    expect(el.textContent).toContain('München');
    expect(el.textContent).toContain('0.612 km');
    expect(el.querySelectorAll('.row').length).toBe(2);
  });

  it('shows inline problem-details errors', async () => {
    const store = TestBed.inject(GeoQueryStore);
    const { GeoApiError } = await import('../core/models');
    api.nearest.mockRejectedValue(
      new GeoApiError({
        title: 'Invalid request',
        status: 400,
        detail: 'count must be an integer in [1, 100]',
      }),
    );
    await store.queryNearest(1, 1);
    const el = render().nativeElement as HTMLElement;
    expect(el.querySelector('.error')!.textContent).toContain('count must be');
  });

  it('dismisses the error via the × button', async () => {
    const store = TestBed.inject(GeoQueryStore);
    const { GeoApiError } = await import('../core/models');
    api.nearest.mockRejectedValue(
      new GeoApiError({
        title: 'Invalid request',
        status: 400,
        detail: 'count must be an integer in [1, 100]',
      }),
    );
    await store.queryNearest(1, 1);
    const fixture = render();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.error')).toBeTruthy();
    (el.querySelector('.dismiss') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(store.error()).toBeNull();
    expect(el.querySelector('.error')).toBeFalsy();
  });

  it('hovering a row sets the store highlight', async () => {
    api.nearest.mockResolvedValue({
      data: {
        count: 1,
        cities: [{ name: 'X', country: 'DE', population: 1, lat: 1, lon: 1, distanceKm: 1 }],
      },
      engineMicros: 1,
      computeCount: 1,
      httpMillis: 1,
    });
    const store = TestBed.inject(GeoQueryStore);
    await store.queryNearest(1, 1);
    const fixture = render();
    const row = fixture.nativeElement.querySelector('.row') as HTMLElement;
    row.dispatchEvent(new MouseEvent('mouseenter'));
    expect(store.highlightedIndex()).toBe(0);
    row.dispatchEvent(new MouseEvent('mouseleave'));
    expect(store.highlightedIndex()).toBeNull();
  });

  it('changing the count input re-runs the current query immediately', async () => {
    api.nearest.mockResolvedValue({
      data: { count: 0, cities: [] },
      engineMicros: 1,
      computeCount: 1,
      httpMillis: 1,
    });
    const store = TestBed.inject(GeoQueryStore);
    await store.queryNearest(48.1, 11.5);
    expect(api.nearest).toHaveBeenCalledTimes(1);

    const fixture = render();
    const input = fixture.nativeElement.querySelector('input[type=number]') as HTMLInputElement;
    input.value = '25';
    input.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(store.count()).toBe(25);
    expect(api.nearest).toHaveBeenCalledTimes(2);
    expect(api.nearest).toHaveBeenLastCalledWith(48.1, 11.5, 25);
  });

  it('count input echoes the clamped value back into the box', async () => {
    api.nearest.mockResolvedValue({
      data: { count: 0, cities: [] },
      engineMicros: 1,
      computeCount: 1,
      httpMillis: 1,
    });
    const fixture = render();
    const input = fixture.nativeElement.querySelector('input[type=number]') as HTMLInputElement;
    input.value = '0';
    input.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(input.value).toBe('1'); // clamped, and the box reflects it
  });

  it('radius slider: input updates the label only, change re-runs the within query', async () => {
    api.within.mockResolvedValue({
      data: { count: 0, cities: [] },
      engineMicros: 1,
      computeCount: 1,
      httpMillis: 1,
    });
    const store = TestBed.inject(GeoQueryStore);
    store.setMode('within');
    await store.queryWithin(48.1, 11.5, 100);
    expect(api.within).toHaveBeenCalledTimes(1);

    const fixture = render();
    const slider = fixture.nativeElement.querySelector('input[type=range]') as HTMLInputElement;

    slider.value = '30';
    slider.dispatchEvent(new Event('input')); // dragging: live label only, no query
    await fixture.whenStable();
    expect(store.radiusKm()).toBe(30);
    expect(api.within).toHaveBeenCalledTimes(1);

    slider.dispatchEvent(new Event('change')); // release: commit + re-query
    await fixture.whenStable();
    expect(api.within).toHaveBeenCalledTimes(2);
    expect(api.within).toHaveBeenLastCalledWith(48.1, 11.5, 30, 0);
  });

  it('min population: change re-queries and echoes the clamped value', async () => {
    api.within.mockResolvedValue({
      data: { count: 0, cities: [] },
      engineMicros: 1,
      computeCount: 1,
      httpMillis: 1,
    });
    const store = TestBed.inject(GeoQueryStore);
    store.setMode('within');
    await store.queryWithin(48.1, 11.5, 100);

    const fixture = render();
    const input = fixture.nativeElement.querySelector(
      'input[type=number][min="0"]',
    ) as HTMLInputElement;
    input.value = '-5';
    input.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(store.minPopulation()).toBe(0);
    expect(input.value).toBe('0'); // clamped echo
    expect(api.within).toHaveBeenCalledTimes(2);
    expect(api.within).toHaveBeenLastCalledWith(48.1, 11.5, 100, 0);
  });
});
