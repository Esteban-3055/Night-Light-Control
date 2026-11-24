Night Light Control 0.5.0 mejora el consumo y corrige fallos de protección,
actualización e instalación.

- Evaluación de curvas sin ordenar ni crear colecciones en cada consulta,
  conservando las intensidades exactas y la continuidad a medianoche.
- Preparación del auxiliar de recuperación en segundo plano, sin bloquear la
  interfaz y antes de aplicar color. Se comprueba también su contenido SHA256.
- Menor frecuencia de comprobación durante desconexiones prolongadas de pantalla,
  conservando la intención de control y reintentando la reconexión.
- Progreso de descarga limitado para evitar saturar la interfaz. Salir cancela
  la transferencia y espera su limpieza, incluso al terminar de recibir datos.
- Límite de tiempo para toda la descarga, incluido un cuerpo de respuesta detenido.
- Validación y extracción completa del paquete en una carpeta temporal antes de
  modificar la instalación; se rechazan rutas duplicadas o inválidas.
- Desinstalación mediante una lista de archivos propios, conservando documentos
  añadidos por el usuario dentro de la carpeta del programa.
- Respaldo del ejecutable anterior también al actualizar una instalación normal.

Las comprobaciones incluyen curvas, transiciones, fallos simulados de pantalla,
recuperación, descargas canceladas o dañadas, paquetes y conservación de archivos.
Los límites y las mediciones están en [la auditoría de esta versión](https://github.com/Esteban-3055/Night-Light-Control/blob/main/docs/AUDITORIA-0.5.0.md).

Para instalar o actualizar, descarga **NightLightControl-0.5.0-Setup.exe**.
También está disponible en la sección **Actualizaciones** de la aplicación.
El ZIP continúa disponible para uso portable. Se conservan los perfiles.
