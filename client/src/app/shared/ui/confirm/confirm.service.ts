import { Injectable, signal } from '@angular/core';

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
}

@Injectable({ providedIn: 'root' })
export class ConfirmService {
  readonly request = signal<ConfirmOptions | null>(null);

  private resolver?: (result: boolean) => void;

  ask(options: ConfirmOptions): Promise<boolean> {
    this.resolver?.(false);
    this.request.set(options);
    return new Promise<boolean>(resolve => {
      this.resolver = resolve;
    });
  }

  resolve(result: boolean): void {
    this.request.set(null);
    const resolver = this.resolver;
    this.resolver = undefined;
    resolver?.(result);
  }
}
