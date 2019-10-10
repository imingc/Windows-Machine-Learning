FROM windowsml:latest

ARG EXE_DIR=.

WORKDIR /app

COPY $EXE_DIR/ ./

CMD [ "CustomVision.exe", "-mCustomVision.onnx", "-iimages", "-v" ]