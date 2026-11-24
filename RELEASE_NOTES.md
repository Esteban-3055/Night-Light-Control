Night Light Control 0.3.0 incorpora guardado explícito, desactivación gradual y
protección frente a cierres inesperados.

- Botón **Guardar curva**, compatible con el guardado automático.
- Fade-out al desactivar, con el mismo ritmo del fade-in y sin saltos al reactivar.
- Indicador de calidez visible hasta terminar la salida.
- Protección opcional con un auxiliar independiente que intenta restaurar el
  color solicitado por Windows cuando se cierra inesperadamente el proceso.
- Corrección de cambios perdidos tras un fallo de escritura y recuperación de
  configuraciones bloqueadas/dañadas con conservación del archivo original.
- Respeto de la vista previa nativa de Windows al desactivar o salir.
- Correcciones en decimales de intensidad, pantalla virtual incompatible y
  entradas incompletas en la lista de versiones de GitHub.

Se conservan los perfiles existentes. Para actualizar desde la aplicación, entra
en **Actualizaciones**, busca la versión y pulsa **Descargar e instalar**.

Las pruebas cubren el motor con pantallas y reloj simulados, fallos de guardado,
procesos de recuperación y renderizado de la interfaz sin control remoto. No se
ha provocado un cierre con cambios de color en las pantallas del usuario. La
recuperación no puede garantizarse si también se termina el auxiliar o Windows
no admite la restauración. Consulta el informe de revisión incluido.
