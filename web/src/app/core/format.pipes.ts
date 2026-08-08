import { Pipe, PipeTransform } from '@angular/core';

@Pipe({ name: 'km' })
export class KmPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return value == null ? '—' : `${value.toFixed(3)} km`;
  }
}

@Pipe({ name: 'engineTime' })
export class EngineTimePipe implements PipeTransform {
  transform(micros: number | null | undefined): string {
    if (micros == null) return '—';
    return micros < 1000 ? `${micros.toFixed(1)} µs` : `${(micros / 1000).toFixed(2)} ms`;
  }
}
