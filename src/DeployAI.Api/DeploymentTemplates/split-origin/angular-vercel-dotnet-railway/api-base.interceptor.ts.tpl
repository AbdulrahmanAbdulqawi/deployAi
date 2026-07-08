import { HttpInterceptorFn } from '@angular/common/http';
import { environment } from '../../../environments/environment';

export const apiBaseInterceptor: HttpInterceptorFn = (req, next) => {
  const base = environment.apiBaseUrl?.replace(/\/$/, '') ?? '';
  if (!base) {
    return next(req);
  }

  const path = req.url.split('?')[0];
  if (/^\/api\//i.test(path) || /^\/hubs\//i.test(path)) {
    return next(req.clone({ url: `${base}${req.url}` }));
  }

  return next(req);
};
