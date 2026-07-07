import { HttpInterceptorFn } from '@angular/common/http';
import { API_BASE_URL } from '../api-base';

export const apiBaseInterceptor: HttpInterceptorFn = (req, next) => {
  const base = API_BASE_URL.replace(/\/$/, '');
  if (!base) {
    return next(req);
  }

  const path = req.url.split('?')[0];
  if (/^\/api\//i.test(path) || /^\/hubs\//i.test(path)) {
    return next(req.clone({ url: `${base}${req.url}` }));
  }

  return next(req);
};
